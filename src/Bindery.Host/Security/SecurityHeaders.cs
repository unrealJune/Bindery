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
