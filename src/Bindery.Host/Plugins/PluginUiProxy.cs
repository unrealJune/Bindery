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
        var target = "/bindery/v1/ui/" + rest.TrimStart('/');

        if (request.QueryString.HasValue)
        {
            target += request.QueryString.Value;
        }

        using var httpClient = client.CreateClient(descriptor.Entry, PluginClient.StreamClient);
        using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), target);

        upstream.Headers.Add("X-Bindery-Base", basePath);
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

            return new ProxiedFragment(
                (int)HttpStatusCode.BadGateway,
                Notice($"{descriptor.DisplayName} is not responding."),
                []);
        }

        using (response)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            // text/html only. Anything else is rejected rather than forwarded: a proxy that
            // will pass through whatever a plugin sets is an open redirect and a content
            // sniffing problem wearing a helpful face.
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

            var body = await ReadCappedAsync(response, timeout.Token);
            var clean = sanitizer.Sanitize(body, descriptor.Name, basePath);

            return new ProxiedFragment((int)response.StatusCode, clean.Html, clean.Removed);
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

    private async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var cap = _options.MaxFragmentBytes;

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

        return Encoding.UTF8.GetString(buffer.ToArray());
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

        return
        [
            .. descriptor.Manifest.Ui.Nav.AsList().Select(entry => new PluginNavEntry(
                descriptor.Name,
                entry.Label,
                basePath + entry.Path,
                entry.Icon.OrNull(),
                entry.Section.OrNull()))
        ];
    }

    /// <summary>Everything the shell needs to render a plugin's navigation entries.</summary>
    public IReadOnlyList<PluginNavEntry> AllNavigation() =>
        [.. registry.Usable.SelectMany(NavigationFor)];
}

public sealed record PluginNavEntry(string Plugin, string Label, string Href, string? Icon, string? Section);

public static class PluginUiEndpoints
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public static IEndpointRouteBuilder MapPluginUi(this IEndpointRouteBuilder builder)
    {
        builder.MapMethods("/plugins/{name}/ui/{**rest}", Methods, ForwardAsync)
            .RequireAuthorization(AuthPolicies.UseUi);

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

        // A browser navigating straight here — a bookmark, a reload, a nav link — wants a
        // page, not the inside of a div. Send it to the shell, which fetches this same URL
        // back with htmx.
        if (HttpMethods.IsGet(context.Request.Method)
            && !context.Request.Headers.ContainsKey("HX-Request"))
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

        var fragment = await proxy.ForwardAsync(
            descriptor,
            context.Request,
            rest ?? string.Empty,
            requestToken,
            cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        return Results.Content(
            PluginUiProxy.Wrap(descriptor.Name, fragment.Html, requestToken),
            "text/html; charset=utf-8",
            Encoding.UTF8,
            fragment.StatusCode);
    }
}
