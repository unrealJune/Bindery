using Bindery.Core;
using Bindery.Host.Catalog;
using Bindery.Host.Downloads;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

public sealed class BookModel(CatalogService catalog, DownloadService downloads) : PageModel
{
    public Domain.Book Book { get; private set; } = default!;

    public IReadOnlyList<Data.DownloadJobEntity> History { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var book = await catalog.FindAsync(id, cancellationToken);

        if (book is null)
        {
            return NotFound();
        }

        Book = book;

        var recent = await downloads.RecentAsync(200, cancellationToken);
        History = [.. recent.Where(job => job.BookId == id)];

        return Page();
    }

    /// <summary>
    /// Asks the source plugin whether there is anything new. Only offered when the book
    /// records where it came from; there is nothing to re-fetch otherwise.
    /// </summary>
    public async Task<IActionResult> OnPostRefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await downloads.EnqueueUpdateAsync(id, User.Identity?.Name, cancellationToken);

        TempData["Notice"] = result.Accepted
            ? "Update queued. Watch the job ledger."
            : result.Problem ?? "That book cannot be updated.";

        return result.Accepted
            ? RedirectToPage("/Downloads", new { highlight = result.Job!.Id })
            : RedirectToPage(new { id });
    }
}
