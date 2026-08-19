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
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' https: data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'none'; " +
        "object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            headers["Content-Security-Policy"] = ContentSecurityPolicy;
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
