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
}
