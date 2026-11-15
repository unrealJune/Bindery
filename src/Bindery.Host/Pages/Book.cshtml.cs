using Bindery.Core;
using Bindery.Host.Catalog;
using Bindery.Host.Downloads;
using Bindery.Host.Library;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Bindery.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

public sealed class BookModel(
    CatalogService catalog,
    DownloadService downloads,
    LibraryWriter library,
    BookSources bookSources,
    PluginRegistry registry,
    SourceLabels sources) : PageModel
{
    public Domain.Book Book { get; private set; } = default!;

    /// <summary>The downloader that filed this book, or "Manual" when nothing did.</summary>
    public string SourceLabel => sources.For(Book.SourcePlugin);

    public IReadOnlyList<Data.DownloadJobEntity> History { get; private set; } = [];

    /// <summary>Every place this book can be fetched from, the followed one first.</summary>
    public IReadOnlyList<SourceView> Sources { get; private set; } = [];

    /// <summary>Other books on this person's shelf that look like this same work.</summary>
    public IReadOnlyList<DuplicateView> Duplicates { get; private set; } = [];

    /// <summary>The rest of this person's shelf, for merging one identity did not suggest.</summary>
    public IReadOnlyList<(Guid Id, string Title, string AuthorLine)> Shelf { get; private set; } = [];

    /// <summary>Downloaders a source can be fetched through, for the "add a source" form.</summary>
    public IReadOnlyList<PluginDescriptor> Downloaders { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var book = await catalog.FindAsync(Identity.SubjectOf(User), id, cancellationToken);

        if (book is null)
        {
            return NotFound();
        }

        Book = book;
        History = await downloads.ForBookAsync(Person.Of(User), id, 50, cancellationToken);

        var owner = Identity.SubjectOf(User);
        Sources = await bookSources.ListAsync(owner, id, cancellationToken);
        Duplicates = await bookSources.DuplicatesAsync(owner, id, cancellationToken);
        Shelf = [.. (await bookSources.ShelfAsync(owner, cancellationToken)).Where(entry => entry.Id != id)];
        Downloaders = [.. registry.Usable.OrderBy(plugin => plugin.DisplayName, StringComparer.OrdinalIgnoreCase)];

        return Page();
    }

    /// <summary>
    /// Asks the source plugin whether there is anything new. Only offered when the book
    /// records where it came from; there is nothing to re-fetch otherwise.
    /// </summary>
    public async Task<IActionResult> OnPostRefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await downloads.EnqueueUpdateAsync(id, User.Identity?.Name, cancellationToken, Identity.SubjectOf(User));
        return Queued(id, result, "Update queued. Watch the job ledger.");
    }

    /// <summary>Downloads a URL into this book, through any plugin.</summary>
    public async Task<IActionResult> OnPostAddSourceAsync(
        Guid id,
        string? sourceUrl,
        string? plugin,
        bool follow,
        CancellationToken cancellationToken)
    {
        var result = await downloads.EnqueueIntoBookAsync(
            id, sourceUrl ?? string.Empty, string.IsNullOrWhiteSpace(plugin) ? null : plugin, follow,
            User.Identity?.Name, cancellationToken, Identity.SubjectOf(User));

        return Queued(id, result, follow
            ? "Fetching into this book; it will follow the new source once it lands."
            : "Fetching into this book as an extra source.");
    }

    /// <summary>
    /// Switches the followed source, then fetches from it so the files come from where the
    /// book is now followed.
    /// </summary>
    public async Task<IActionResult> OnPostFollowAsync(Guid id, int sourceId, CancellationToken cancellationToken)
    {
        var problem = await bookSources.FollowAsync(Identity.SubjectOf(User), id, sourceId, cancellationToken);

        if (problem is not null)
        {
            TempData["Notice"] = problem;
            return RedirectToPage(new { id });
        }

        var result = await downloads.EnqueueSourceAsync(
            id, sourceId, follow: true, User.Identity?.Name, cancellationToken, Identity.SubjectOf(User));

        return Queued(id, result, "Updates now follow that source. Fetching from it now.");
    }

    /// <summary>Re-fetches one source into the book without changing which is followed.</summary>
    public async Task<IActionResult> OnPostFetchSourceAsync(Guid id, int sourceId, CancellationToken cancellationToken)
    {
        var result = await downloads.EnqueueSourceAsync(
            id, sourceId, follow: false, User.Identity?.Name, cancellationToken, Identity.SubjectOf(User));

        return Queued(id, result, "Fetching that source into this book.");
    }

    public async Task<IActionResult> OnPostRemoveSourceAsync(Guid id, int sourceId, CancellationToken cancellationToken)
    {
        var problem = await bookSources.RemoveAsync(Identity.SubjectOf(User), id, sourceId, cancellationToken);
        TempData["Notice"] = problem ?? "Source removed. The book's files are unchanged.";
        return RedirectToPage(new { id });
    }

    /// <summary>Folds another book into this one; this book keeps its identity.</summary>
    public async Task<IActionResult> OnPostMergeAsync(
        Guid id,
        Guid other,
        string? keep,
        bool follow,
        CancellationToken cancellationToken)
    {
        var mode = string.Equals(keep, "other", StringComparison.OrdinalIgnoreCase) ? MergeKeep.Other : MergeKeep.Target;
        var result = await library.MergeAsync(Identity.SubjectOf(User), id, other, mode, follow, cancellationToken);

        TempData["Notice"] = result.Merged
            ? "Merged. Every source of both books now lands here."
            : result.Problem ?? "Those books could not be merged.";

        return RedirectToPage(new { id });
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

    private IActionResult Queued(Guid id, EnqueueResult result, string success)
    {
        TempData["Notice"] = result.Accepted ? success : result.Problem ?? "That could not be queued.";

        return result.Accepted
            ? RedirectToPage("/Downloads", new { highlight = result.Job!.Id })
            : RedirectToPage(new { id });
    }
}

/// <summary>What the merge form for one suggested duplicate needs.</summary>
public sealed record MergeFormModel(Guid TargetId, Guid OtherId, string OtherTitle);
