using System.Net;
using System.Text;
using Bindery.Core;
using Bindery.Host.Configuration;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Plugins;

public sealed record ProxiedFragment(int StatusCode, string Html, IReadOnlyList<string> Removed);

/// <summary>
/// A sandboxed plugin's own bytes, forwarded verbatim.
/// </summary>
/// <param name="Failure">
/// A human-readable reason the plugin's response was refused, or <c>null</c> when
/// <paramref name="Body"/> is the plugin's own. The frame renders it rather than the host
/// chrome, because the failure belongs to the plugin's panel.
/// </param>
public sealed record ProxiedDocument(
    int StatusCode,
    string ContentType,
    byte[] Body,
    string? Failure = null);

/// <summary>
/// The reverse proxy behind <c>/plugins/{name}/ui/{**rest}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Method, query string, and body are forwarded verbatim. What comes back is checked to be
/// HTML, size-capped, sanitized, and handed to the caller — which wraps it in a container
/// carrying the antiforgery token so a plugin cannot get CSRF wrong.
/// </para>
/// <para>
/// What deliberately does <em>not</em> happen here: rewriting <c>href</c>, <c>action</c>, or
/// <c>hx-*</c> values to point at the mount path. Bindery sends
/// <c>X-Bindery-Base</c> and the plugin builds its own URLs from it. Server-side URL
/// rewriting looks tidy in a demo and then breaks at 2am on the one relative URL in the one
/// attribute nobody thought about.
/// </para>
/// </remarks>
public sealed class PluginUiProxy(
    PluginClient client,
    PluginRegistry registry,
    FragmentSanitizer sanitizer,
    IOptions<BinderyOptions> options,
    ILogger<PluginUiProxy> logger)
{
    private static readonly string[] ForwardedRequestHeaders = ["Accept-Language"];

    /// <summary>
    /// What a sandboxed plugin is allowed to answer with.
    /// </summary>
    /// <remarks>
    /// Wider than the fragment tier's <c>text/html</c> because a sandboxed plugin serves its
    /// own scripts, styles, and images through this path. It is still an allowlist: a
    /// content type nobody named is refused rather than forwarded, so the proxy cannot be
    /// talked into becoming a general-purpose open relay for arbitrary bytes.
    /// </remarks>
    private static readonly HashSet<string> SandboxedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "text/css",
        "text/javascript",
        "application/javascript",
        "application/json",
        "text/plain",
        "image/svg+xml",
        "text/event-stream"
    };

    private static bool IsAllowedSandboxedType(string mediaType) =>
        SandboxedContentTypes.Contains(mediaType)
        || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || mediaType.StartsWith("font/", StringComparison.OrdinalIgnoreCase);

    private readonly PluginHostOptions _options = options.Value.Plugins;

    public string BasePathFor(string plugin) => $"/plugins/{plugin}/ui";

    public async Task<ProxiedFragment> ForwardAsync(
        PluginDescriptor descriptor,
        HttpRequest request,
        string rest,
        string csrfToken,
        CancellationToken cancellationToken)
    {
        if (descriptor.Manifest is null || !descriptor.Manifest.Ui.Mode.ServesHtml)
        {
            return new ProxiedFragment(
                (int)HttpStatusCode.NotFound,
                Notice($"{descriptor.DisplayName} does not provide its own interface."),
                []);
        }

        var basePath = BasePathFor(descriptor.Name);

        return await SendAsync(
            descriptor,
            request,
            rest,
            csrfToken,
            async (response, token) =>
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

                // text/html only. Anything else is rejected rather than forwarded: a proxy
                // that will pass through whatever a plugin sets is an open redirect and a
                // content sniffing problem wearing a helpful face.
                if (!mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                    && !mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "plugin {Plugin} answered a UI request with '{ContentType}'", descriptor.Name, mediaType);

                    return new ProxiedFragment(
                        (int)HttpStatusCode.BadGateway,
                        Notice($"{descriptor.DisplayName} returned something that is not HTML."),
                        []);
                }

                var body = await ReadCappedAsync(response, _options.MaxFragmentBytes, token);
                var clean = sanitizer.Sanitize(Encoding.UTF8.GetString(body), descriptor.Name, basePath);

                return new ProxiedFragment((int)response.StatusCode, clean.Html, clean.Removed);
            },
            failure => new ProxiedFragment((int)HttpStatusCode.BadGateway, Notice(failure), []),
            cancellationToken);
    }

    /// <summary>
    /// Forwards a sandboxed plugin's response verbatim.
    /// </summary>
    /// <remarks>
    /// No sanitizer runs here, and that is the point of the tier rather than an oversight.
    /// These bytes are handed to an iframe with no <c>allow-same-origin</c>, so the document
    /// they build has an opaque origin: no cookie, no parent DOM, no storage, and — via
    /// <c>connect-src 'none'</c> — no network. There is no authority in reach for injected
    /// script to abuse, so there is nothing for a sanitizer to protect. What the host still
    /// enforces is the content-type allowlist and the size cap.
    /// </remarks>
    public async Task<ProxiedDocument> ForwardDocumentAsync(
        PluginDescriptor descriptor,
        HttpRequest request,
        string rest,
        string csrfToken,
        CancellationToken cancellationToken)
    {
        if (descriptor.Manifest is null || !descriptor.Manifest.Ui.Mode.RunsInFrame)
        {
            return Refused(HttpStatusCode.NotFound, $"{descriptor.DisplayName} does not provide its own interface.");
        }

        return await SendAsync(
            descriptor,
            request,
            rest,
            csrfToken,
            async (response, token) =>
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

                if (!IsAllowedSandboxedType(mediaType))
                {
                    logger.LogWarning(
                        "plugin {Plugin} answered a sandboxed UI request with '{ContentType}'",
                        descriptor.Name,
                        mediaType);

                    return Refused(
                        HttpStatusCode.BadGateway,
                        $"{descriptor.DisplayName} returned an unsupported content type.");
                }

                var body = await ReadCappedAsync(response, _options.MaxSandboxedBytes, token);
                var charset = response.Content.Headers.ContentType?.CharSet;

                var contentType = string.IsNullOrWhiteSpace(charset)
                    ? mediaType
                    : $"{mediaType}; charset={charset}";

                return new ProxiedDocument((int)response.StatusCode, contentType, body);
            },
            failure => Refused(HttpStatusCode.BadGateway, failure),
            cancellationToken);
    }

    /// <summary>
    /// A refusal rendered as a minimal document, so the plugin's own panel shows the
    /// problem instead of the host chrome having to know about it.
    /// </summary>
    private static ProxiedDocument Refused(HttpStatusCode status, string message) =>
        new(
            (int)status,
            "text/html; charset=utf-8",
            Encoding.UTF8.GetBytes(
                "<!doctype html><html><head><meta charset=\"utf-8\">"
                + "<link rel=\"stylesheet\" href=\"/css/bindery.css\"></head>"
                + $"<body><div class=\"bnd-card bnd-notice\"><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>"),
            message);

    /// <summary>
    /// The request half both tiers share: build it, send it, and turn a plugin that is not
    /// answering into a value rather than an exception.
    /// </summary>
    private async Task<T> SendAsync<T>(
        PluginDescriptor descriptor,
        HttpRequest request,
        string rest,
        string csrfToken,
        Func<HttpResponseMessage, CancellationToken, Task<T>> onResponse,
        Func<string, T> onFailure,
        CancellationToken cancellationToken)
    {
        var target = "/bindery/v1/ui/" + rest.TrimStart('/');

        if (request.QueryString.HasValue)
        {
            target += request.QueryString.Value;
        }

        using var httpClient = client.CreateClient(descriptor.Entry, PluginClient.StreamClient);
        using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), target);

        upstream.Headers.Add("X-Bindery-Base", BasePathFor(descriptor.Name));
        upstream.Headers.Add("X-Bindery-Csrf", csrfToken);

        foreach (var header in ForwardedRequestHeaders)
        {
            if (request.Headers.TryGetValue(header, out var value))
            {
                upstream.Headers.TryAddWithoutValidation(header, value.ToArray());
            }
        }

        if (HasBody(request))
        {
            // Buffered rather than streamed: the body is a form post from a page, and a
            // bounded copy is what lets the request be size-capped at all.
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            upstream.Content = new ByteArrayContent(buffer.ToArray());

            if (request.ContentType is not null)
            {
                upstream.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.FragmentTimeout);

        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "plugin {Plugin} did not answer a UI request for {Path}", descriptor.Name, rest);

            return onFailure($"{descriptor.DisplayName} is not responding.");
        }

        using (response)
        {
            return await onResponse(response, timeout.Token);
        }
    }

    /// <summary>
    /// Wraps a sanitized fragment in the container that carries the antiforgery token.
    /// </summary>
    /// <remarks>
    /// htmx picks <c>hx-headers</c> up by inheritance, so every request a plugin's markup
    /// makes is covered without the plugin doing anything — and, more to the point, without
    /// it being able to do anything wrong. <c>hx-headers</c> is refused by the sanitizer, so
    /// a fragment cannot override what is set here.
    /// </remarks>
    public static string Wrap(string plugin, string fragmentHtml, string csrfToken)
    {
        var headers = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["X-CSRF-TOKEN"] = csrfToken
        });

        var builder = new StringBuilder();
        builder.Append("<div id=\"bnd-plugin-ui\" data-plugin=\"");
        builder.Append(WebUtility.HtmlEncode(plugin));
        builder.Append("\" hx-headers='");
        builder.Append(WebUtility.HtmlEncode(headers));
        builder.Append("'>");
        builder.Append(fragmentHtml);
        builder.Append("</div>");
        return builder.ToString();
    }

    public static string Notice(string message) =>
        $"<div class=\"bnd-card bnd-notice\"><p>{WebUtility.HtmlEncode(message)}</p></div>";

    private async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, int cap, CancellationToken cancellationToken)
    {
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

            if (buffer.Length + read > cap)
            {
                // Truncating markup would produce an unbalanced fragment; refusing it
                // produces an honest error.
                throw new PluginTransportException($"a plugin fragment exceeded the {cap} byte limit");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool HasBody(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method)
        && !HttpMethods.IsHead(request.Method)
        && !HttpMethods.IsDelete(request.Method);

    public IReadOnlyList<PluginNavEntry> NavigationFor(PluginDescriptor descriptor)
    {
        if (descriptor.Manifest is null || !descriptor.Manifest.Ui.Mode.ServesHtml)
        {
            return [];
        }

        var basePath = BasePathFor(descriptor.Name);
        var framed = descriptor.Manifest.Ui.Mode.RunsInFrame;

        return
        [
            .. descriptor.Manifest.Ui.Nav.AsList().Select(entry => new PluginNavEntry(
                descriptor.Name,
                entry.Label,
                // A sandboxed plugin's screens are linked through its desk page, never
                // straight at the document. The raw URL is the iframe's `src`: opened at the
                // top level it has no parent, so the bridge never hands it an `init` and the
                // plugin sits there loading forever. A fragment plugin has no such problem —
                // its markup is the page.
                framed
                    ? $"/plugins/{Uri.EscapeDataString(descriptor.Name)}?path={Uri.EscapeDataString(entry.Path)}"
                    : basePath + entry.Path,
                entry.Icon.OrNull(),
                entry.Section.OrNull(),
                entry.Path))
        ];
    }

    /// <summary>Everything the shell needs to render a plugin's navigation entries.</summary>
    public IReadOnlyList<PluginNavEntry> AllNavigation() =>
        [.. registry.Usable.SelectMany(NavigationFor)];
}

public sealed record PluginNavEntry(
    string Plugin,
    string Label,
    string Href,
    string? Icon,
    string? Section,
    string Path);

public static class PluginUiEndpoints
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public static IEndpointRouteBuilder MapPluginUi(this IEndpointRouteBuilder builder)
    {
        builder.MapMethods("/plugins/{name}/ui/{**rest}", Methods, ForwardAsync)
            .RequireAuthorization(AuthPolicies.UseFrame);

        return builder;
    }

    private static async Task<IResult> ForwardAsync(
        string name,
        string? rest,
        HttpContext context,
        PluginRegistry registry,
        PluginUiProxy proxy,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(name);

        if (descriptor is null || !descriptor.IsUsable)
        {
            return Results.NotFound();
        }

        var sandboxed = descriptor.Manifest?.Ui.Mode.RunsInFrame == true;

        // A browser navigating straight here — a bookmark, a reload, a nav link — wants a
        // page, not the inside of a div. Send it to the shell, which fetches this same URL
        // back with htmx.
        //
        // For a sandboxed plugin this URL is the iframe's `src`, so the redirect must fire
        // for a top-level navigation and *not* for the frame loading itself — otherwise the
        // frame bounces to the page that contains it. `Sec-Fetch-Dest` is exactly that
        // distinction: `document` for the address bar, `iframe` for the frame. A browser too
        // old to send it falls through to serving the document, which is the safer miss.
        var topLevel = string.Equals(
            context.Request.Headers["Sec-Fetch-Dest"].ToString(),
            "document",
            StringComparison.OrdinalIgnoreCase);

        if (HttpMethods.IsGet(context.Request.Method)
            && !context.Request.Headers.ContainsKey("HX-Request")
            && (!sandboxed || topLevel))
        {
            var path = "/" + (rest ?? string.Empty).TrimStart('/');
            return Results.LocalRedirect($"/plugins/{Uri.EscapeDataString(name)}?path={Uri.EscapeDataString(path)}");
        }

        if (!HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method))
        {
            await antiforgery.ValidateRequestAsync(context);
        }

        var tokens = antiforgery.GetAndStoreTokens(context);
        var requestToken = tokens.RequestToken
            ?? throw new InvalidOperationException("antiforgery did not issue a request token");

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        if (sandboxed)
        {
            var document = await proxy.ForwardDocumentAsync(
                descriptor,
                context.Request,
                rest ?? string.Empty,
                requestToken,
                cancellationToken);

            var origin = $"{context.Request.Scheme}://{context.Request.Host}";

            // Replaces the host page's policy for this response only. The frame gets to run
            // its own script; it just has nothing to run it against.
            context.Response.Headers["Content-Security-Policy"] =
                SecurityHeaders.BuildFrameContentSecurityPolicy(origin);

            // The global header is DENY, which would stop Bindery framing its own plugin.
            // frame-ancestors 'self' in the policy above is the replacement, and it is the
            // one modern browsers honour.
            context.Response.Headers.Remove("X-Frame-Options");

            // Results.Bytes has no status-code overload, and the status matters: a plugin's
            // own 404 or 500 must reach the frame rather than being flattened to 200.
            context.Response.StatusCode = document.StatusCode;
            context.Response.ContentType = document.ContentType;
            await context.Response.Body.WriteAsync(document.Body, cancellationToken);

            return Results.Empty;
        }

        var fragment = await proxy.ForwardAsync(
            descriptor,
            context.Request,
            rest ?? string.Empty,
            requestToken,
            cancellationToken);

        return Results.Content(
            PluginUiProxy.Wrap(descriptor.Name, fragment.Html, requestToken),
            "text/html; charset=utf-8",
            Encoding.UTF8,
            fragment.StatusCode);
    }
}
