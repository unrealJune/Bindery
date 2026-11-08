using System.Security.Claims;

namespace Bindery.Host.Security;

/// <summary>Who a signed-in principal is, in the two forms the host stores.</summary>
public static class Identity
{
    /// <summary>
    /// The stable identity to key per-person data on. The same precedence feed tokens use, so
    /// a person's cards and their devices agree on who they belong to.
    /// </summary>
    public static string SubjectOf(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? user.Identity?.Name
        ?? "unknown";

    /// <summary>The address the identity provider vouched for, if any.</summary>
    public static string? EmailOf(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("email")?.Value;

    /// <summary>A name to show for a person, if the identity provider gave one.</summary>
    public static string? DisplayNameOf(ClaimsPrincipal user) =>
        user.FindFirst("name")?.Value
        ?? user.FindFirst(ClaimTypes.Name)?.Value
        ?? user.FindFirst("preferred_username")?.Value;

    /// <summary>
    /// Whether this person administers the installation: install-wide plugin settings,
    /// library rescans, everyone's downloads. Not whether they can see everyone's books —
    /// nobody can.
    /// </summary>
    public static bool IsAdmin(ClaimsPrincipal user) =>
        user.HasClaim(BinderyClaims.Role, BinderyClaims.AdminRole);

    /// <summary>The wire value of <see cref="IsAdmin"/>, as sent to plugins (protocol §2.3).</summary>
    public static string RoleOf(ClaimsPrincipal user) =>
        IsAdmin(user) ? BinderyClaims.AdminRole : BinderyClaims.MemberRole;
}

/// <summary>
/// Who is asking, reduced to what the services decide on. Built from the principal at the
/// edge so services never see claims, and never confuse "signed in" with "may do this".
/// </summary>
public sealed record Person(string Subject, bool IsAdmin)
{
    public static Person Of(ClaimsPrincipal user) => new(Identity.SubjectOf(user), Identity.IsAdmin(user));
}
