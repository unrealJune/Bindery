using Bindery.Host.Data;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

/// <summary>
/// Feed tokens: the credential an ereader can actually hold.
/// </summary>
/// <remarks>
/// The secret is shown exactly once, on the redirect after issuing it. It is never stored
/// in a form the UI could render again, so "show me that token" is genuinely impossible
/// rather than merely discouraged.
/// </remarks>
public sealed class FeedTokensModel(FeedTokenService tokens) : PageModel
{
    public IReadOnlyList<FeedTokenEntity> Tokens { get; private set; } = [];

    public string? IssuedSecret { get; private set; }

    public string? IssuedName { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        IssuedSecret = TempData["IssuedSecret"] as string;
        IssuedName = TempData["IssuedName"] as string;
        Tokens = await tokens.ListAsync(Identity.SubjectOf(User), cancellationToken);
    }

    public async Task<IActionResult> OnPostIssueAsync(string name, CancellationToken cancellationToken)
    {
        var issued = await tokens.IssueAsync(name ?? string.Empty, Identity.SubjectOf(User), cancellationToken);

        TempData["IssuedSecret"] = issued.Secret;
        TempData["IssuedName"] = issued.Entity.Name;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        TempData["Notice"] = await tokens.RevokeAsync(id, Identity.SubjectOf(User), cancellationToken)
            ? "Token revoked. That device will stop syncing on its next request."
            : "That token could not be revoked.";

        return RedirectToPage();
    }
}
