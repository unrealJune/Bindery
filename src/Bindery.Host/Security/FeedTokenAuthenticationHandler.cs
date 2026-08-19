using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Security;

public sealed class FeedTokenOptions : AuthenticationSchemeOptions
{
    /// <summary>Sent in the Basic challenge. Some readers show it in the login prompt.</summary>
    public string Realm { get; set; } = "Bindery";
}

/// <summary>
/// Authenticates OPDS requests with a feed token, presented as a bearer token or as an
/// HTTP Basic password.
/// </summary>
/// <remarks>
/// Two mechanisms is not gold-plating. Bearer is what the protocol suggests; Basic is what
/// KOReader, Moon+ Reader, and Aldiko actually send, because their catalog settings screen
/// has a username and a password box and nothing else. Supporting only the tidy one means
/// supporting no real client.
/// </remarks>
public sealed class FeedTokenAuthenticationHandler(
    IOptionsMonitor<FeedTokenOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    FeedTokenService tokens) : AuthenticationHandler<FeedTokenOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "FeedToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ExtractToken();

        if (presented is null)
        {
            return AuthenticateResult.NoResult();
        }

        var token = await tokens.ValidateAsync(presented, Context.RequestAborted);

        if (token is null)
        {
            Logger.LogInformation("rejected a feed token from {Address}", Context.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("Unknown or revoked feed token.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, token.Subject),
                new Claim(ClaimTypes.Name, token.Name),
                new Claim(BinderyClaims.TokenId, token.Id.ToString()),
                // Marks the principal as catalog-only. The UI policy checks for its
                // absence, so a feed token can never open a settings page.
                new Claim(BinderyClaims.CredentialKind, BinderyClaims.FeedTokenCredential)
            ],
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private string? ExtractToken()
    {
        var header = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header["Bearer ".Length..].Trim();
            return value.Length == 0 ? null : value;
        }

        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var separator = decoded.IndexOf(':');

            if (separator < 0)
            {
                return null;
            }

            // The username is ignored: the token is the credential, and readers put
            // whatever they like in the other box.
            var password = decoded[(separator + 1)..];
            return password.Length == 0 ? null : password;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Basic, not Bearer: a reader that gets a Bearer challenge shows nothing useful,
        // whereas Basic makes it prompt for the credentials it can actually send.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{Options.Realm}\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}

public static class BinderyClaims
{
    public const string TokenId = "bindery:token";

    public const string CredentialKind = "bindery:credential";

    public const string FeedTokenCredential = "feed-token";

    public const string SessionCredential = "session";
}
