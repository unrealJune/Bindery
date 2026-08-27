using System.Security.Claims;
using System.Text.Encodings.Web;
using Bindery.Host.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Bindery.Host.Security;

public static class AuthPolicies
{
    /// <summary>Browse and download the catalog. A session or a feed token.</summary>
    public const string ReadCatalog = "bindery:read-catalog";

    /// <summary>Use the web interface. A session only — never a feed token.</summary>
    public const string UseUi = "bindery:use-ui";

    /// <summary>
    /// Load a sandboxed plugin's own document and subresources.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="UseUi"/> because of a cookie subtlety with real teeth: the
    /// session cookie is <c>SameSite=Lax</c>, and a request initiated by an opaque-origin
    /// frame has no site, so the browser treats it as cross-site and withholds it. The
    /// frame's initial navigation is fine — the parent initiates that — but every
    /// subresource the plugin's document then asks for would arrive unauthenticated and
    /// bounce to the sign-in page. This policy also accepts the narrowly scoped frame
    /// cookie, which exists to survive exactly that.
    /// </remarks>
    public const string UseFrame = "bindery:use-frame";
}

/// <summary>
/// Authentication as it stands in <c>PLAN.md</c> §8: OIDC for people, feed tokens for
/// devices, and a loud development escape hatch.
/// </summary>
public static class AuthSetup
{
    public const string DevSchemeName = "Development";

    /// <summary>
    /// The cookie a sandboxed plugin's frame carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists only because <c>SameSite=None</c> is the one setting that survives a
    /// request initiated by an opaque origin, and putting the main session cookie in that
    /// mode to solve it would trade a plugin inconvenience for a site-wide CSRF weakening.
    /// So this is a second, deliberately small credential instead: scoped by
    /// <c>Path=/plugins</c>, accepted by exactly one route, and useless anywhere else.
    /// </para>
    /// <para>
    /// <c>SameSite=None</c> requires <c>Secure</c>, so this cookie is not issued over plain
    /// HTTP. That is only a development concern, and development runs
    /// <c>Auth:Mode=None</c>, where none of this applies.
    /// </para>
    /// </remarks>
    public const string FrameSchemeName = "FrameCookie";

    public static IServiceCollection AddBinderyAuth(this IServiceCollection services, BinderyOptions options)
    {
        var auth = options.Auth;

        if (auth.Mode == AuthMode.None)
        {
            services.AddAuthentication(DevSchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevSchemeName, _ => { });

            services.AddAuthorizationBuilder()
                .AddPolicy(AuthPolicies.ReadCatalog, policy => policy.RequireAssertion(_ => true))
                .AddPolicy(AuthPolicies.UseUi, policy => policy.RequireAssertion(_ => true))
                .AddPolicy(AuthPolicies.UseFrame, policy => policy.RequireAssertion(_ => true));

            return services;
        }

        services.AddAuthentication(auth =>
            {
                auth.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                auth.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, cookie =>
            {
                cookie.Cookie.Name = "bindery.session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = options.Auth.SessionLifetime;
                cookie.SlidingExpiration = true;
                cookie.LoginPath = "/signin";
                cookie.AccessDeniedPath = "/denied";
            })
            .AddCookie(FrameSchemeName, cookie =>
            {
                cookie.Cookie.Name = "bindery.frame";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.None;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.Path = "/plugins";
                cookie.ExpireTimeSpan = options.Auth.SessionLifetime;
                cookie.SlidingExpiration = true;

                // A frame subresource that has lost its cookie should fail as a frame
                // subresource, not redirect a plugin's script tag to the sign-in page.
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
            {
                oidc.Authority = auth.Authority;
                oidc.ClientId = auth.ClientId;
                oidc.ClientSecret = auth.ClientSecret;
                oidc.RequireHttpsMetadata = auth.RequireHttpsMetadata;

                // Authorization code with PKCE. Generic on purpose: Entra, Authentik,
                // Keycloak, and Dex all work from the same four settings.
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.SaveTokens = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.CallbackPath = "/signin-oidc";
                oidc.SignedOutCallbackPath = "/signout-callback-oidc";
                oidc.MapInboundClaims = false;

                oidc.Scope.Clear();

                foreach (var scope in auth.Scopes)
                {
                    oidc.Scope.Add(scope);
                }

                oidc.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = auth.NameClaim,
                    RoleClaimType = "roles"
                };

                oidc.Events.OnTicketReceived = context =>
                {
                    var principal = context.Principal;

                    if (principal is not null)
                    {
                        // Distinguishes a browser session from a feed token so the UI
                        // policy can refuse the latter.
                        var identity = (ClaimsIdentity)principal.Identity!;
                        identity.AddClaim(new Claim(BinderyClaims.CredentialKind, BinderyClaims.SessionCredential));

                        if (options.Auth.AllowedUsers.Count > 0 && !IsAllowed(principal, options.Auth))
                        {
                            context.Fail("This account is not on the allow list.");
                            return Task.CompletedTask;
                        }
                    }

                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.ReadCatalog, policy => policy
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, FeedTokenAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser())
            .AddPolicy(AuthPolicies.UseUi, policy => policy
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    // Belt and braces on the rule that matters most in §5: a credential a
                    // device holds must never reach a page that can change settings.
                    context.User.FindFirst(BinderyClaims.CredentialKind)?.Value != BinderyClaims.FeedTokenCredential))
            .AddPolicy(AuthPolicies.UseFrame, policy => policy
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, FrameSchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    context.User.FindFirst(BinderyClaims.CredentialKind)?.Value != BinderyClaims.FeedTokenCredential));

        services.AddAuthentication()
            .AddScheme<FeedTokenOptions, FeedTokenAuthenticationHandler>(
                FeedTokenAuthenticationHandler.SchemeName,
                feed => feed.Realm = options.CatalogTitle);

        return services;
    }

    private static bool IsAllowed(ClaimsPrincipal principal, AuthOptions auth)
    {
        var identifiers = new[]
        {
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            principal.FindFirst("sub")?.Value,
            principal.FindFirst(ClaimTypes.Email)?.Value,
            principal.FindFirst("email")?.Value,
            principal.FindFirst("preferred_username")?.Value
        };

        return identifiers.Any(value =>
            value is not null && auth.AllowedUsers.Contains(value, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Authenticates everyone as a local developer. Only reachable with
/// <c>Auth:Mode=None</c>, which warns at boot and keeps warning.
/// </summary>
public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "dev"),
                new Claim(ClaimTypes.Name, "Development"),
                new Claim(BinderyClaims.CredentialKind, BinderyClaims.SessionCredential)
            ],
            AuthSetup.DevSchemeName);

        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSetup.DevSchemeName)));
    }
}
