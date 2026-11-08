using Bindery.Core;
using Bindery.Host.Catalog;
using Bindery.Host.Downloads;
using Bindery.Host.Library;
using Bindery.Host.Security;
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
        var book = await catalog.FindAsync(Identity.SubjectOf(User), id, cancellationToken);

        if (book is null)
        {
            return NotFound();
        }

        Book = book;
        History = await downloads.ForBookAsync(Person.Of(User), id, 50, cancellationToken);

        return Page();
    }

    /// <summary>
    /// Asks the source plugin whether there is anything new. Only offered when the book
    /// records where it came from; there is nothing to re-fetch otherwise.
    /// </summary>
    public async Task<IActionResult> OnPostRefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await downloads.EnqueueUpdateAsync(id, User.Identity?.Name, cancellationToken, Identity.SubjectOf(User));

        TempData["Notice"] = result.Accepted
            ? "Update queued. Watch the job ledger."
            : result.Problem ?? "That book cannot be updated.";

        return result.Accepted
            ? RedirectToPage("/Downloads", new { highlight = result.Job!.Id })
            : RedirectToPage(new { id });
    }

    /// <summary>
    /// Takes the book off this person's shelf, and deletes its files if nobody else has it.
    /// </summary>
    /// <remarks>
    /// There is no undo and no trash: the volume is the library, so the last owner removing
    /// a book really does delete the files. The confirmation lives in the markup — a
    /// disclosure the operator has to open before the button exists — rather than a dialog,
    /// because the UI ships no JavaScript for this. The notice is the same either way;
    /// whether anyone else has the book is theirs to know.
    /// </remarks>
    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var owner = Identity.SubjectOf(User);
        var book = await catalog.FindAsync(owner, id, cancellationToken);

        if (book is null || !await library.DeleteAsync(id, owner, cancellationToken))
        {
            return NotFound();
        }

        TempData["Notice"] = $"Removed “{book.Title}” from your shelf.";
        return RedirectToPage("/Library");
    }
}
