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
}

/// <summary>
/// Authentication as it stands in <c>PLAN.md</c> §8: OIDC for people, feed tokens for
/// devices, and a loud development escape hatch.
/// </summary>
public static class AuthSetup
{
    public const string DevSchemeName = "Development";

    public static IServiceCollection AddBinderyAuth(this IServiceCollection services, BinderyOptions options)
    {
        var auth = options.Auth;

        if (auth.Mode == AuthMode.None)
        {
            services.AddAuthentication(DevSchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevSchemeName, _ => { });

            services.AddAuthorizationBuilder()
                .AddPolicy(AuthPolicies.ReadCatalog, policy => policy.RequireAssertion(_ => true))
                .AddPolicy(AuthPolicies.UseUi, policy => policy.RequireAssertion(_ => true));

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
