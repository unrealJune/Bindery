using Bindery.Host.Configuration;

namespace Bindery.Host.Security;

/// <summary>
/// The response headers that make hosting plugin HTML defensible.
/// </summary>
/// <remarks>
/// The Content-Security-Policy is the second of the three required layers around plugin
/// fragments. It has no <c>unsafe-inline</c> anywhere, which is why Bindery's own scripts
/// and styles are external files and why there is not a single inline handler in the Razor
/// pages: the moment one appears, someone will be tempted to loosen this instead.
/// </remarks>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, BinderyOptions options)
    {
        // Built once at startup, not per request: the policy is a pure function of config,
        // and rebuilding a constant string on every response is waste.
        var contentSecurityPolicy = BuildContentSecurityPolicy(options);

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            headers["Content-Security-Policy"] = contentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers["X-Frame-Options"] = "DENY";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";

            // Feeds and downloads are per-user content behind auth; a shared cache holding
            // them is a cross-account leak waiting for a proxy to be introduced.
            if (context.Request.Path.StartsWithSegments("/opds") || context.Request.Path.StartsWithSegments("/plugins"))
            {
                headers["Cache-Control"] = "private, no-store";
            }

            await next();
        });
    }

    /// <summary>
    /// The policy applied to a sandboxed plugin's own document, replacing the host page's
    /// for that response only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>'unsafe-inline'</c> and <c>'unsafe-eval'</c> appear here and nowhere else, and
    /// they carry none of their usual meaning: the document they apply to has an opaque
    /// origin, so it cannot reach Bindery's cookies, DOM, storage, or API no matter what
    /// script runs in it. Injecting script into a context that holds no authority achieves
    /// nothing. This is the whole point of the tier — see <c>docs/PLUGIN-UI.md</c> §5.
    /// </para>
    /// <para>
    /// <c>connect-src 'none'</c> is the load-bearing line. It removes <c>fetch</c>,
    /// <c>XMLHttpRequest</c>, <c>EventSource</c>, WebSocket, and <c>sendBeacon</c>, which is
    /// what makes the postMessage bridge the <em>only</em> way out of the frame rather than
    /// merely the recommended one. A compromised plugin cannot phone home.
    /// </para>
    /// <para>
    /// <paramref name="origin"/> is Bindery's own origin, named explicitly because
    /// <c>'self'</c> matches nothing from an opaque origin. It is what lets the plugin load
    /// its own subresources back through the proxy.
    /// </para>
    /// </remarks>
    public static string BuildFrameContentSecurityPolicy(string origin) =>
        string.Join("; ",
            "default-src 'none'",
            $"script-src 'unsafe-inline' 'unsafe-eval' {origin}",
            $"style-src 'unsafe-inline' {origin}",
            $"img-src data: blob: {origin}",
            $"font-src data: {origin}",
            $"media-src data: blob: {origin}",
            "connect-src 'none'",
            "form-action 'none'",
            // Replaces the global X-Frame-Options: DENY, which would otherwise stop Bindery
            // framing its own plugin. Only Bindery may embed it.
            "frame-ancestors 'self'",
            "base-uri 'none'",
            "object-src 'none'",
            "frame-src 'none'");

    /// <summary>
    /// Assembles the policy. Everything except <c>form-action</c> is fixed; see
    /// <see cref="BuildFormAction"/> for why that one has to be deployment-aware.
    /// </summary>
    internal static string BuildContentSecurityPolicy(BinderyOptions options) =>
        string.Join("; ",
            "default-src 'self'",
            "script-src 'self'",
            "style-src 'self'",
            "img-src 'self' https: data:",
            "font-src 'self'",
            "connect-src 'self'",
            $"form-action {BuildFormAction(options)}",
            "frame-ancestors 'none'",
            "base-uri 'none'",
            "object-src 'none'");

    /// <summary>
    /// <c>'self'</c>, the OIDC authority, and any extra configured origins.
    /// </summary>
    /// <remarks>
    /// The authority is derived rather than configured because OIDC redirect login cannot
    /// work without it: signing in POSTs the sign-in form to Bindery, which answers with a
    /// 302 to the authority's authorize endpoint, and browsers apply <c>form-action</c> to
    /// every hop of a form submission's redirect chain — not just the initial target. With a
    /// bare <c>'self'</c> the browser silently drops that navigation, so the user sees a
    /// button that does nothing and the server log stays empty because the request is never
    /// sent. Deriving it keeps a correct deployment correct without anyone having to know
    /// this; <see cref="SecurityOptions.FormActionSources"/> covers the rest.
    /// </remarks>
    internal static string BuildFormAction(BinderyOptions options)
    {
        var sources = new List<string> { "'self'" };

        if (options.Auth.Mode == AuthMode.Oidc && TryGetOrigin(options.Auth.Authority, out var authority))
        {
            sources.Add(authority);
        }

        foreach (var configured in options.Security.FormActionSources)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                continue;
            }

            var source = configured.Trim();
            if (!sources.Contains(source, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(source);
            }
        }

        return string.Join(' ', sources);
    }

    /// <summary>
    /// Reduces an authority URL to the scheme://host[:port] origin a CSP source expects. A
    /// path (authentik's authority carries one) is not a valid source expression, and a
    /// default port must be omitted or it will not match.
    /// </summary>
    private static bool TryGetOrigin(string? url, out string origin)
    {
        origin = string.Empty;

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        origin = parsed.IsDefaultPort
            ? $"{parsed.Scheme}://{parsed.Host}"
            : $"{parsed.Scheme}://{parsed.Host}:{parsed.Port}";

        return true;
    }
}
