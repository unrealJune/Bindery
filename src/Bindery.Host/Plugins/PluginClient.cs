using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Bindery.Core;
using Bindery.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Plugins;

/// <summary>Raised when a plugin misbehaves at the transport level.</summary>
public sealed class PluginTransportException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record FetchedArtifact(string TempPath, long SizeBytes, string Sha256, string ContentType);

/// <summary>
/// The HTTP client for the plugin protocol.
/// </summary>
/// <remarks>
/// This is the only place in the host that speaks to a plugin. Wire parsing belongs to
/// <c>Bindery.Core.Protocol</c>; what lives here is the transport and its limits — the
/// timeouts, size caps, and the fact that a client disconnect is how cancellation is
/// expressed.
/// </remarks>
public sealed class PluginClient(
    IHttpClientFactory factory,
    IOptions<BinderyOptions> options,
    PluginNotifyTokens notifyTokens,
    ILogger<PluginClient> logger)
{
    public const string RequestClient = "bindery-plugin";
    public const string StreamClient = "bindery-plugin-stream";

    private const int MaxManifestBytes = 256 * 1024;
    private const int MaxNdjsonLineBytes = 64 * 1024;

    private readonly PluginHostOptions _options = options.Value.Plugins;
    private readonly UpdateOptions _updates = options.Value.Updates;

    // ------------------------------------------------------------ manifest

    public async Task<Protocol.Manifest> GetManifestAsync(PluginEntry entry, CancellationToken cancellationToken)
    {
        using var client = CreateClient(entry, RequestClient);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ManifestTimeout);

        var body = await ReadBoundedStringAsync(
            client, HttpMethod.Get, "/bindery/v1/manifest", null, MaxManifestBytes, timeout.Token);

        if (!Protocol.ManifestModule.parse(body).TryGet(out var manifest, out var error))
        {
            throw new PluginTransportException($"plugin '{entry.Name}' served an unusable manifest: {error}");
        }

        if (!string.Equals(manifest.Name, entry.Name, StringComparison.Ordinal))
        {
            // Configuration and manifest disagreeing is a deployment bug, and trusting
            // either side silently would route URLs to the wrong container.
            throw new PluginTransportException(
                $"configured plugin '{entry.Name}' identifies itself as '{manifest.Name}'");
        }

        return manifest;
    }

    public async Task<bool> IsHealthyAsync(PluginEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(entry, RequestClient);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ManifestTimeout);

            using var response = await client.GetAsync("/healthz", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ probe

    /// <summary>
    /// Asks a plugin whether it handles a URL. A probe that fails is not fatal — the
    /// caller falls back to the manifest's patterns, exactly as the protocol requires.
    /// </summary>
    public async Task<Protocol.ProbeResult?> ProbeAsync(PluginEntry entry, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(entry, RequestClient);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ProbeTimeout);

            var body = await ReadBoundedStringAsync(
                client,
                HttpMethod.Post,
                "/bindery/v1/probe",
                new StringContent(Protocol.Request.probe(url), Encoding.UTF8, "application/json"),
                64 * 1024,
                timeout.Token);

            if (Protocol.Probe.parse(body).TryGet(out var result, out var error))
            {
                return result;
            }

            logger.LogWarning("plugin {Plugin} probe response was unusable: {Error}", entry.Name, error);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or PluginTransportException)
        {
            logger.LogDebug(ex, "plugin {Plugin} probe failed; falling back to manifest patterns", entry.Name);
            return null;
        }
    }

    // ------------------------------------------------------------ download

    /// <summary>
    /// Runs a download, invoking <paramref name="onEvent"/> for each streamed event.
    /// </summary>
    /// <remarks>
    /// Cancelling <paramref name="cancellationToken"/> disposes the response, which drops
    /// the connection — and a dropped connection is precisely how the protocol says a job
    /// is cancelled. There is no separate cancel handshake to get out of sync.
    /// </remarks>
    public async Task DownloadAsync(
        PluginEntry entry,
        Protocol.DownloadRequest request,
        Func<Protocol.PluginEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(entry, StreamClient);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/bindery/v1/download")
        {
            Content = new StringContent(Protocol.Request.download(request), Encoding.UTF8, "application/json")
        };

        message.Headers.Add("X-Bindery-Job-Id", request.JobId.ToString("D"));

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(response, cancellationToken);
            throw new PluginTransportException(
                $"plugin '{entry.Name}' refused the download with {(int)response.StatusCode}: {detail}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (!string.Equals(mediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginTransportException(
                $"plugin '{entry.Name}' answered download with '{mediaType}' rather than application/x-ndjson");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        var sawResult = false;

        while (true)
        {
            // The idle timeout is per event, not per download: a large fetch legitimately
            // runs for many minutes, but a silent one is wedged.
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(_options.DownloadIdleTimeout(options.Value));

            string? line;

            try
            {
                line = await ReadLineAsync(reader, idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PluginTransportException(
                    $"plugin '{entry.Name}' sent nothing for {options.Value.Downloads.IdleTimeout.TotalSeconds:0}s");
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!Protocol.Event.parse(line).TryGet(out var pluginEvent, out var error))
            {
                throw new PluginTransportException($"plugin '{entry.Name}' sent an unusable event: {error}");
            }

            if (pluginEvent.IsResult)
            {
                if (sawResult)
                {
                    throw new PluginTransportException($"plugin '{entry.Name}' sent more than one result event");
                }

                sawResult = true;
            }

            await onEvent(pluginEvent, cancellationToken);
        }

        if (!sawResult)
        {
            throw new PluginTransportException($"plugin '{entry.Name}' ended the stream without a result event");
        }
    }

    /// <summary>
    /// Streams an artifact to a temporary file, hashing as it goes.
    /// </summary>
    /// <remarks>
    /// The size cap is enforced while reading rather than by trusting the artifact's
    /// declared <c>bytes</c> or a Content-Length header, both of which are a plugin's
    /// claim about itself.
    /// </remarks>
    public async Task<FetchedArtifact> FetchArtifactAsync(
        PluginEntry entry,
        Guid jobId,
        Protocol.Artifact artifact,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(entry, StreamClient);

        var path = $"/bindery/v1/artifacts/{Uri.EscapeDataString(jobId.ToString("D"))}/{Uri.EscapeDataString(artifact.Id)}";

        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.Add("X-Bindery-Job-Id", jobId.ToString("D"));

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new PluginTransportException(
                $"plugin '{entry.Name}' returned {(int)response.StatusCode} for artifact '{artifact.Id}'");
        }

        Directory.CreateDirectory(stagingDirectory);
        var tempPath = Path.Combine(stagingDirectory, $"{jobId:N}-{Sanitize(artifact.Id)}.part");

        var limit = options.Value.Downloads.MaxArtifactBytes;
        long total = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(tempPath))
        using (var hasher = SHA256.Create())
        {
            var buffer = new byte[81920];

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);

                if (read == 0)
                {
                    break;
                }

                total += read;

                if (total > limit)
                {
                    target.Close();
                    TryDelete(tempPath);
                    throw new PluginTransportException(
                        $"artifact '{artifact.Id}' from '{entry.Name}' exceeded the {limit} byte limit");
                }

                hasher.TransformBlock(buffer, 0, read, null, 0);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            hasher.TransformFinalBlock([], 0, 0);
            var digest = Convert.ToHexString(hasher.Hash!).ToLowerInvariant();

            var declared = artifact.Sha256.OrNull();

            if (declared is not null && !string.Equals(declared, digest, StringComparison.Ordinal))
            {
                target.Close();
                TryDelete(tempPath);
                throw new PluginTransportException(
                    $"artifact '{artifact.Id}' from '{entry.Name}' failed its own sha256 check");
            }

            var contentType = artifact.ContentType.OrNull()
                ?? response.Content.Headers.ContentType?.ToString()
                ?? Domain.Formats.contentType(artifact.Format);

            return new FetchedArtifact(tempPath, total, digest, contentType);
        }
    }

    /// <summary>
    /// Tells a plugin to forget a job. Best effort: the protocol requires plugins to expire
    /// artifacts themselves, so a failure here leaks nothing permanent.
    /// </summary>
    public async Task CancelJobAsync(PluginEntry entry, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(entry, RequestClient);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var request = new HttpRequestMessage(
                HttpMethod.Delete, $"/bindery/v1/jobs/{Uri.EscapeDataString(jobId.ToString("D"))}");

            request.Headers.Add("X-Bindery-Job-Id", jobId.ToString("D"));

            using var response = await client.SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "plugin {Plugin} returned {Status} deleting job {Job}", entry.Name, (int)response.StatusCode, jobId);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(ex, "could not tell {Plugin} to clean up job {Job}", entry.Name, jobId);
        }
    }

    // ------------------------------------------------------------ actions

    public async Task<Protocol.ActionOutcome> InvokeActionAsync(
        PluginEntry entry,
        string action,
        IReadOnlyDictionary<string, string> input,
        IReadOnlyDictionary<string, string> config,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(entry, RequestClient);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ActionTimeout);

        var payload = Protocol.Request.action(action, input.ToFSharpMap(), config.ToFSharpMap());

        var body = await ReadBoundedStringAsync(
            client,
            HttpMethod.Post,
            $"/bindery/v1/actions/{Uri.EscapeDataString(action)}",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            1024 * 1024,
            timeout.Token);

        if (!Protocol.Action.parse(body).TryGet(out var outcome, out var error))
        {
            throw new PluginTransportException($"plugin '{entry.Name}' sent an unusable action response: {error}");
        }

        return outcome;
    }

    // ------------------------------------------------------------ plumbing

    public HttpClient CreateClient(PluginEntry entry, string clientName)
    {
        var client = factory.CreateClient(clientName);

        if (!Uri.TryCreate(entry.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new PluginTransportException($"plugin '{entry.Name}' has an unusable base URL: {entry.BaseUrl}");
        }

        client.BaseAddress = baseUri;

        var token = entry.Token ?? _options.Token;

        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // Protocol 3.7. Advertised on every request rather than handed over once, because a
        // plugin holds no durable relationship with a particular host process: a restart
        // mints a new token, and the next request is what tells the plugin about it. Both
        // headers are omitted when the scheduler is off, which is how a plugin learns the
        // host accepts no notifications.
        if (_updates.Enabled && !string.IsNullOrWhiteSpace(_updates.NotifyUrl))
        {
            client.DefaultRequestHeaders.Remove("X-Bindery-Notify");
            client.DefaultRequestHeaders.Remove("X-Bindery-Notify-Token");
            client.DefaultRequestHeaders.Add("X-Bindery-Notify", _updates.NotifyUrl);
            client.DefaultRequestHeaders.Add("X-Bindery-Notify-Token", notifyTokens.For(entry.Name));
        }

        return client;
    }

    private async Task<string> ReadBoundedStringAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        HttpContent? content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(response, cancellationToken);
            throw new PluginTransportException($"{method} {path} returned {(int)response.StatusCode}: {detail}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new PluginTransportException($"{method} {path} exceeded the {maxBytes} byte limit");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads one NDJSON line, refusing to buffer past the protocol's per-line limit.
    /// </summary>
    /// <remarks>
    /// <c>StreamReader.ReadLineAsync</c> would grow without bound on a plugin that never
    /// sends a newline, which is a memory exhaustion bug wearing a convenience method.
    /// </remarks>
    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(256);
        var single = new char[1];

        while (true)
        {
            var read = await reader.ReadAsync(single.AsMemory(0, 1), cancellationToken);

            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            if (single[0] == '\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            builder.Append(single[0]);

            if (builder.Length > MaxNdjsonLineBytes)
            {
                throw new PluginTransportException($"an NDJSON line exceeded {MaxNdjsonLineBytes} bytes");
            }
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return text.Length > 500 ? text[..500] : text;
        }
        catch
        {
            return "<no body>";
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover .part file in staging is swept on the next boot.
        }
    }
}

internal static class PluginHostOptionsExtensions
{
    public static TimeSpan DownloadIdleTimeout(this PluginHostOptions _, BinderyOptions options) =>
        options.Downloads.IdleTimeout;
}
