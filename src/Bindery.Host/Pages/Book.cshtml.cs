using Bindery.Core;
using Bindery.Host.Catalog;
using Bindery.Host.Downloads;
using Bindery.Host.Library;
using Bindery.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

public sealed class BookModel(
    CatalogService catalog,
    DownloadService downloads,
    LibraryWriter library,
    SourceLabels sources) : PageModel
{
    public Domain.Book Book { get; private set; } = default!;

    /// <summary>The downloader that filed this book, or "Manual" when nothing did.</summary>
    public string SourceLabel => sources.For(Book.SourcePlugin);

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

    /// <summary>
    /// Removes the book's files and its index rows.
    /// </summary>
    /// <remarks>
    /// There is no undo and no trash: the volume is the library, so this really does delete
    /// the files. The confirmation lives in the markup — a disclosure the operator has to
    /// open before the button exists — rather than a dialog, because the UI ships no
    /// JavaScript for this.
    /// </remarks>
    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var book = await catalog.FindAsync(id, cancellationToken);

        if (book is null)
        {
            return NotFound();
        }

        await library.DeleteAsync(id, cancellationToken);

        TempData["Notice"] = $"Removed “{book.Title}” from the volume.";
        return RedirectToPage("/Library");
    }
}
