using System.Security.Claims;
using Bindery.Host.Configuration;
using Bindery.Host.Library;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Security;

/// <summary>
/// Decides, on every request, whether the person making it is an admin — and notes that
/// they were here.
/// </summary>
/// <remarks>
/// <para>
/// The role is derived on every request rather than stored in the session cookie, so a
/// change to <c>AdminClaim</c>/<c>AdminValues</c> applies to the next request, and sessions
/// issued before roles existed get one. The group claims it reads, though, are the ones
/// the identity provider issued at sign-in and are carried in the cookie: taking someone
/// out of the admin group at the provider takes effect when they next sign in or their
/// session ends, not before.
/// </para>
/// <para>
/// Feed tokens are left alone. A device only ever reads its owner's catalog, so it has no
/// use for a role, and skipping it keeps the OPDS hot path free of extra queries.
/// </para>
/// </remarks>
public sealed class BinderyClaimsTransformation(
    IOptions<BinderyOptions> options,
    UserDirectory users,
    ShelfService shelves,
    ILogger<BinderyClaimsTransformation> logger) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true } identity
            || principal.FindFirst(BinderyClaims.CredentialKind)?.Value == BinderyClaims.FeedTokenCredential)
        {
            return principal;
        }

        var subject = Identity.SubjectOf(principal);

        if (subject == "unknown")
        {
            return principal;
        }

        await RecordAsync(principal, subject);

        // The development scheme states its own role (and the test host's stand-in can be
        // told to be a member); there is no identity provider to ask.
        if (identity.AuthenticationType == AuthSetup.DevSchemeName && principal.HasClaim(claim => claim.Type == BinderyClaims.Role))
        {
            return principal;
        }

        var role = await IsAdminAsync(principal, subject) ? BinderyClaims.AdminRole : BinderyClaims.MemberRole;

        var transformed = principal.Clone();

        // Never trust a role that arrived with the credential — the frame cookie, for
        // instance, is a copy of a principal this transformation had already decorated.
        foreach (var claimsIdentity in transformed.Identities)
        {
            foreach (var stale in claimsIdentity.FindAll(BinderyClaims.Role).ToList())
            {
                claimsIdentity.RemoveClaim(stale);
            }
        }

        ((ClaimsIdentity)transformed.Identity!).AddClaim(new Claim(BinderyClaims.Role, role));
        return transformed;
    }

    private async Task<bool> IsAdminAsync(ClaimsPrincipal principal, string subject)
    {
        var auth = options.Value.Auth;

        if (auth.AdminValues.Count > 0)
        {
            return principal.FindAll(auth.AdminClaim)
                .Any(claim => auth.AdminValues.Contains(claim.Value, StringComparer.OrdinalIgnoreCase));
        }

        // Nobody named as admin: the founder is, so a single-person installation keeps
        // being administrable across the upgrade that introduced roles.
        return await users.FounderAsync(CancellationToken.None) == subject;
    }

    private async Task RecordAsync(ClaimsPrincipal principal, string subject)
    {
        try
        {
            var isNew = await users.TouchAsync(
                subject, Identity.DisplayNameOf(principal), Identity.EmailOf(principal), CancellationToken.None);

            // The very first person in owns whatever was on the volume before them.
            if (isNew && await users.FounderAsync(CancellationToken.None) == subject)
            {
                await shelves.AdoptOrphansAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bookkeeping. It must never be the reason someone cannot sign in.
            logger.LogWarning(ex, "could not record a visit by {Subject}", subject);
        }
    }
}
