using Bindery.Core;
using Bindery.Host.Data;
using Bindery.Host.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Library;

/// <summary>One source of a book, as the book page and the API show it.</summary>
public sealed record SourceView(
    int Id,
    string Plugin,
    string PluginLabel,
    bool PluginUsable,
    string Url,
    string? SourceId,
    bool Followed,
    bool CurrentFiles,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastFetchedAt);

/// <summary>Another book that looks like the same work.</summary>
public sealed record DuplicateView(Guid BookId, string Title, string AuthorLine, double Score, string Reason, bool Certain);

/// <summary>
/// Where a book comes from: every source it is known by, which one is followed for updates,
/// and which other books look like the same work.
/// </summary>
/// <remarks>
/// <para>
/// The rules for "same work" are <c>Bindery.Core.WorkIdentity</c>; this class only fetches
/// what they need from the index. Changing which source is followed goes through the sidecar
/// first and the index second, like every other write to a book, so a rescan agrees — and
/// under <see cref="ShelfService.HoldAsync"/>, so a shelf change in between is not lost.
/// </para>
/// <para>
/// Everything a person asks for is scoped to their shelf: a book they do not hold is "not
/// found", and duplicates are only ever suggested from among their own books. What is on
/// anyone else's shelf is theirs to know. The one global question is
/// <see cref="FindBookAsync"/> — "is this source already in the library?" — which is how a
/// download lands on the shared copy instead of beside it, exactly as before sources.
/// </para>
/// </remarks>
public sealed class BookSources(
    BinderyDbContext db,
    LibraryStore library,
    BookIndexer indexer,
    PluginRegistry registry,
    ILogger<BookSources> logger)
{
    public async Task<IReadOnlyList<SourceView>> ListAsync(string owner, Guid bookId, CancellationToken cancellationToken)
    {
        var book = await db.Books
            .AsNoTracking()
            .Include(candidate => candidate.Sources)
            .Where(candidate => candidate.Shelf.Any(entry => entry.Owner == owner))
            .FirstOrDefaultAsync(candidate => candidate.Id == bookId, cancellationToken);

        if (book is null)
        {
            return [];
        }

        var latestFetch = book.Sources.Max(source => source.LastFetchedAt);

        return [.. book.Sources
            .OrderByDescending(source => IsFollowed(book, source))
            .ThenBy(source => source.AddedAt)
            .Select(source =>
            {
                var descriptor = registry.Find(source.Plugin);

                return new SourceView(
                    source.Id,
                    source.Plugin,
                    descriptor?.DisplayName ?? source.Plugin,
                    descriptor?.IsUsable == true,
                    source.Url,
                    source.SourceId,
                    IsFollowed(book, source),
                    latestFetch is not null && source.LastFetchedAt == latestFetch,
                    source.AddedAt,
                    source.LastFetchedAt);
            })];
    }

    /// <summary>Is this source the one updates come from?</summary>
    public static bool IsFollowed(BookEntity book, BookSourceEntity source) =>
        book.SourcePlugin == source.Plugin
        && book.SourceUrl is not null
        && WorkIdentity.normalizeUrl(book.SourceUrl) == source.NormalizedUrl;

    /// <summary>
    /// The book already known by one of these source identities — a URL (as pasted, or as
    /// the plugin reported it) or a plugin's source id. This is the certain kind of match:
    /// a download that hits it is filed into that book, never beside it.
    /// </summary>
    public async Task<BookEntity?> FindBookAsync(
        IEnumerable<string?> urls,
        string? plugin,
        string? sourceId,
        CancellationToken cancellationToken)
    {
        var normalized = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => WorkIdentity.normalizeUrl(url!))
            .Distinct()
            .ToList();

        var match = await db.BookSources
            .AsNoTracking()
            .Where(source => normalized.Contains(source.NormalizedUrl)
                             || (sourceId != null && source.SourceId == sourceId && source.Plugin == plugin))
            // The same plugin's claim is the stronger one when two books both match.
            .OrderByDescending(source => source.Plugin == plugin)
            .Select(source => (Guid?)source.BookId)
            .FirstOrDefaultAsync(cancellationToken);

        if (match is null && sourceId is not null && plugin is not null)
        {
            // Books indexed before sources existed, and not rescanned since.
            match = await db.Books
                .AsNoTracking()
                .Where(book => book.SourcePlugin == plugin && book.SourceId == sourceId)
                .Select(book => (Guid?)book.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return match is null
            ? null
            : await db.Books
                .Include(book => book.Sources)
                .FirstOrDefaultAsync(book => book.Id == match, cancellationToken);
    }

    /// <summary>Other books that look like the same work, strongest first.</summary>
    public async Task<IReadOnlyList<DuplicateView>> DuplicatesAsync(string owner, Guid bookId, CancellationToken cancellationToken)
    {
        var works = await WorksAsync(owner, cancellationToken);
        var me = works.FirstOrDefault(work => work.Work.Id == bookId);

        if (me is null)
        {
            return [];
        }

        var byId = works.ToDictionary(work => work.Work.Id);

        return [.. WorkIdentity.duplicatesOf(me.Work, works.Select(work => work.Work))
            .Select(pair =>
            {
                var (other, likeness) = pair.ToValueTuple();
                return new DuplicateView(
                    other.Id,
                    other.Title,
                    byId[other.Id].AuthorLine,
                    WorkIdentity.LikenessModule.score(likeness),
                    WorkIdentity.LikenessModule.reason(likeness),
                    WorkIdentity.LikenessModule.isCertain(likeness));
            })];
    }

    /// <summary>Every book, title-sorted, for choosing a merge partner by hand.</summary>
    public async Task<IReadOnlyList<(Guid Id, string Title, string AuthorLine)>> ShelfAsync(string owner, CancellationToken cancellationToken) =>
        [.. (await WorksAsync(owner, cancellationToken))
            .OrderBy(work => work.Work.Title, StringComparer.OrdinalIgnoreCase)
            .Select(work => (work.Work.Id, work.Work.Title, work.AuthorLine))];

    /// <summary>
    /// Makes one of the book's sources the followed one. Returns a problem, or null.
    /// </summary>
    /// <remarks>
    /// Only the bookkeeping: the book's files are whatever was last fetched, from whichever
    /// source. Callers that want the files to come from the newly followed source queue a
    /// fetch afterwards (the book page and the API both do, by default).
    /// </remarks>
    public async Task<string?> FollowAsync(string owner, Guid bookId, int sourceId, CancellationToken cancellationToken)
    {
        var book = await LoadAsync(owner, bookId, cancellationToken);
        var source = book?.Sources.FirstOrDefault(candidate => candidate.Id == sourceId);

        if (book is null || source is null)
        {
            return "That source is not one of this book's.";
        }

        if (IsFollowed(book, source))
        {
            return null;
        }

        await RewriteAsync(book, sidecar => sidecar with
        {
            SourcePlugin = source.Plugin,
            SourceUrl = source.Url,
            SourceId = source.SourceId
        }, cancellationToken);

        logger.LogInformation("{Title} now follows {Plugin} at {Url}", book.Title, source.Plugin, source.Url);
        return null;
    }

    /// <summary>Forgets a source that is not followed. Returns a problem, or null.</summary>
    public async Task<string?> RemoveAsync(string owner, Guid bookId, int sourceId, CancellationToken cancellationToken)
    {
        var book = await LoadAsync(owner, bookId, cancellationToken);
        var source = book?.Sources.FirstOrDefault(candidate => candidate.Id == sourceId);

        if (book is null || source is null)
        {
            return "That source is not one of this book's.";
        }

        if (IsFollowed(book, source))
        {
            return "That is the followed source. Follow another one before removing it.";
        }

        var key = $"{source.Plugin} {source.NormalizedUrl}";

        await RewriteAsync(book, sidecar => sidecar with
        {
            Sources = [.. sidecar.AllSources().Where(candidate => candidate.Key != key)]
        }, cancellationToken);

        return null;
    }

    /// <summary>
    /// Fills in sources for books indexed before sources existed: their followed source.
    /// Idempotent, cheap when there is nothing to do, and run once at startup. A rescan
    /// would do the same from the sidecars; this saves needing one after the upgrade.
    /// </summary>
    public static async Task BackfillAsync(BinderyDbContext db, CancellationToken cancellationToken)
    {
        var missing = await db.Books
            .Where(book => book.SourcePlugin != null && book.SourceUrl != null && !book.Sources.Any())
            .Select(book => new { book.Id, book.SourcePlugin, book.SourceUrl, book.SourceId, book.AddedAt, book.UpdatedAt })
            .ToListAsync(cancellationToken);

        foreach (var book in missing)
        {
            db.BookSources.Add(new BookSourceEntity
            {
                BookId = book.Id,
                Plugin = book.SourcePlugin!,
                Url = book.SourceUrl!,
                NormalizedUrl = WorkIdentity.normalizeUrl(book.SourceUrl!),
                SourceId = book.SourceId,
                AddedAt = book.AddedAt,
                LastFetchedAt = book.UpdatedAt
            });
        }

        if (missing.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // ------------------------------------------------------------ internals

    private sealed record IndexedWork(WorkIdentity.Work Work, string AuthorLine);

    private async Task<List<IndexedWork>> WorksAsync(string owner, CancellationToken cancellationToken)
    {
        // One person's shelf, and only the columns identity reads. Thousands of rows at
        // most; this runs when a book page opens or a download lands.
        var rows = await db.Books
            .AsNoTracking()
            .Where(book => book.Shelf.Any(entry => entry.Owner == owner))
            .Select(book => new
            {
                book.Id,
                book.Title,
                book.SourceId,
                Authors = book.Authors.OrderBy(author => author.Order).Select(author => author.Author.Name).ToList(),
                Urls = book.Sources.Select(source => source.Url).ToList(),
                Ids = book.Sources.Where(source => source.SourceId != null).Select(source => source.Plugin + "|" + source.SourceId).ToList()
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new IndexedWork(
            new WorkIdentity.Work(
                row.Id,
                row.Title,
                row.Authors.ToFSharpList(),
                row.Urls.ToFSharpList(),
                row.Ids.ToFSharpList()),
            row.Authors.Count > 0 ? string.Join(", ", row.Authors) : "Unknown"))];
    }

    private Task<BookEntity?> LoadAsync(string owner, Guid bookId, CancellationToken cancellationToken) =>
        db.Books
            .Include(book => book.Sources)
            .Where(book => book.Shelf.Any(entry => entry.Owner == owner))
            .FirstOrDefaultAsync(book => book.Id == bookId, cancellationToken);

    /// <summary>Sidecar first, index second: the order every write to a book follows.</summary>
    private async Task RewriteAsync(
        BookEntity book,
        Func<SidecarMetadata, SidecarMetadata> change,
        CancellationToken cancellationToken)
    {
        // Read-modify-write of a sidecar that also carries the owners: held against shelf
        // changes, so nobody shelving the book meanwhile is written back out of it.
        using (await ShelfService.HoldAsync(cancellationToken))
        {
            var sidecar = await library.ReadSidecarAsync(book.DirectoryPath, cancellationToken)
                          ?? throw new InvalidOperationException($"{book.DirectoryPath} has no readable sidecar");

            var updated = change(sidecar with { Sources = sidecar.AllSources() });
            await library.WriteSidecarAsync(book.DirectoryPath, updated, cancellationToken);
            await indexer.IndexAsync(updated, book.DirectoryPath, book, cancellationToken);
        }
    }
}
