using Bindery.Core;
using Bindery.Host.Data;
using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Catalog;

public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageIndex, int PageSize)
{
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / Math.Max(1, PageSize));

    /// <summary>1-based, as OpenSearch counts.</summary>
    public int StartIndex => (PageIndex * PageSize) + 1;

    public bool HasNext => PageIndex + 1 < PageCount;

    public bool HasPrevious => PageIndex > 0;
}

/// <summary>
/// Reading the library.
/// </summary>
/// <remarks>
/// <para>
/// Everything here returns <c>Bindery.Core.Domain</c> types rather than EF entities, so
/// that feed generation is a pure function of data and the OPDS serializers never see a
/// tracked entity or a lazy load.
/// </para>
/// <para>
/// Every method takes the owner whose catalog it is reading, and sees only the books on
/// that person's shelf — groupings and counts included, so an author someone else reads
/// does not appear as an empty entry in yours. There is deliberately no overload without
/// one: the library is shared on disk, never in a catalog.
/// </para>
/// </remarks>
public sealed class CatalogService(BinderyDbContext db)
{
    private IQueryable<BookEntity> Books(string owner) =>
        db.Books
            .AsNoTracking()
            .Where(book => book.Shelf.Any(entry => entry.Owner == owner))
            .Include(book => book.Authors).ThenInclude(link => link.Author)
            .Include(book => book.Tags).ThenInclude(link => link.Tag)
            .Include(book => book.Files)
            .Include(book => book.Series);

    public async Task<Page<Domain.Book>> RecentAsync(string owner, int page, int size, CancellationToken cancellationToken) =>
        await PageAsync(Books(owner).OrderByDescending(book => book.AddedAt), page, size, cancellationToken);

    public async Task<Page<Domain.Book>> AllAsync(string owner, int page, int size, CancellationToken cancellationToken) =>
        await PageAsync(Books(owner).OrderBy(book => book.SortTitle), page, size, cancellationToken);

    public async Task<Page<Domain.Book>> SearchAsync(
        string owner,
        string query,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var term = (query ?? string.Empty).Trim();

        if (term.Length == 0)
        {
            return new Page<Domain.Book>([], 0, 0, size);
        }

        var pattern = $"%{Escape(term)}%";

        // LIKE over three columns rather than FTS: the library is thousands of rows, not
        // millions, and full-text search is explicitly out of scope for v1 (PLAN.md §10).
        var matches = Books(owner).Where(book =>
            EF.Functions.Like(book.Title, pattern)
            || (book.Summary != null && EF.Functions.Like(book.Summary, pattern))
            || book.Authors.Any(link => EF.Functions.Like(link.Author.Name, pattern))
            || book.Tags.Any(link => EF.Functions.Like(link.Tag.Name, pattern))
            || (book.Series != null && EF.Functions.Like(book.Series.Name, pattern)));

        return await PageAsync(matches.OrderBy(book => book.SortTitle), page, size, cancellationToken);
    }

    public async Task<Page<Domain.Book>> ByAuthorAsync(
        string owner,
        int authorId,
        int page,
        int size,
        CancellationToken cancellationToken) =>
        await PageAsync(
            Books(owner).Where(book => book.Authors.Any(link => link.AuthorId == authorId))
                 .OrderBy(book => book.SortTitle),
            page,
            size,
            cancellationToken);

    public async Task<Page<Domain.Book>> ByTagAsync(
        string owner,
        int tagId,
        int page,
        int size,
        CancellationToken cancellationToken) =>
        await PageAsync(
            Books(owner).Where(book => book.Tags.Any(link => link.TagId == tagId)).OrderBy(book => book.SortTitle),
            page,
            size,
            cancellationToken);

    public async Task<Page<Domain.Book>> BySeriesAsync(
        string owner,
        int seriesId,
        int page,
        int size,
        CancellationToken cancellationToken) =>
        await PageAsync(
            Books(owner).Where(book => book.SeriesId == seriesId)
                 .OrderBy(book => book.SeriesIndex ?? double.MaxValue)
                 .ThenBy(book => book.SortTitle),
            page,
            size,
            cancellationToken);

    /// <summary>A book on <paramref name="owner"/>'s shelf; null for any other, existing or not.</summary>
    public async Task<Domain.Book?> FindAsync(string owner, Guid id, CancellationToken cancellationToken)
    {
        var entity = await Books(owner).FirstOrDefaultAsync(book => book.Id == id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public Task<BookEntity?> FindEntityAsync(string owner, Guid id, CancellationToken cancellationToken) =>
        Books(owner).FirstOrDefaultAsync(book => book.Id == id, cancellationToken);

    public Task<int> CountAsync(string owner, CancellationToken cancellationToken) =>
        db.Books.CountAsync(book => book.Shelf.Any(entry => entry.Owner == owner), cancellationToken);

    public async Task<IReadOnlyList<Domain.Grouping>> AuthorsAsync(string owner, CancellationToken cancellationToken) =>
        await db.Authors
            .AsNoTracking()
            .Select(author => new
            {
                author.Id,
                author.Name,
                author.SortName,
                Count = author.Books.Count(link => link.Book.Shelf.Any(entry => entry.Owner == owner))
            })
            .Where(author => author.Count > 0)
            .OrderBy(author => author.SortName)
            .Select(author => new Domain.Grouping(
                author.Id.ToString(), author.Name, author.SortName, author.Count))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Domain.Grouping>> TagsAsync(string owner, CancellationToken cancellationToken) =>
        await db.Tags
            .AsNoTracking()
            .Select(tag => new
            {
                tag.Id,
                tag.Name,
                Count = tag.Books.Count(link => link.Book.Shelf.Any(entry => entry.Owner == owner))
            })
            .Where(tag => tag.Count > 0)
            .OrderBy(tag => tag.Name)
            .Select(tag => new Domain.Grouping(tag.Id.ToString(), tag.Name, tag.Name, tag.Count))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Domain.Grouping>> SeriesAsync(string owner, CancellationToken cancellationToken) =>
        await db.Series
            .AsNoTracking()
            .Select(series => new
            {
                series.Id,
                series.Name,
                Count = series.Books.Count(book => book.Shelf.Any(entry => entry.Owner == owner))
            })
            .Where(series => series.Count > 0)
            .OrderBy(series => series.Name)
            .Select(series => new Domain.Grouping(
                series.Id.ToString(), series.Name, series.Name, series.Count))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// An author with at least one book on <paramref name="owner"/>'s shelf. Someone else's
    /// author is as absent as one that does not exist, so ids cannot be probed.
    /// </summary>
    public Task<AuthorEntity?> FindAuthorAsync(string owner, int id, CancellationToken cancellationToken) =>
        db.Authors.AsNoTracking().FirstOrDefaultAsync(
            author => author.Id == id
                && author.Books.Any(link => link.Book.Shelf.Any(entry => entry.Owner == owner)),
            cancellationToken);

    public Task<TagEntity?> FindTagAsync(string owner, int id, CancellationToken cancellationToken) =>
        db.Tags.AsNoTracking().FirstOrDefaultAsync(
            tag => tag.Id == id
                && tag.Books.Any(link => link.Book.Shelf.Any(entry => entry.Owner == owner)),
            cancellationToken);

    public Task<SeriesEntity?> FindSeriesAsync(string owner, int id, CancellationToken cancellationToken) =>
        db.Series.AsNoTracking().FirstOrDefaultAsync(
            series => series.Id == id
                && series.Books.Any(book => book.Shelf.Any(entry => entry.Owner == owner)),
            cancellationToken);

    /// <summary>
    /// The most recent change to anything on this shelf, used as a feed's <c>updated</c>.
    /// </summary>
    /// <remarks>
    /// Shelving a book someone else already had changes this person's catalog without
    /// changing the book, so the shelf entry's own time counts too — otherwise a reader
    /// caching on <c>updated</c> would never see it arrive.
    /// </remarks>
    public async Task<DateTimeOffset> LastModifiedAsync(string owner, CancellationToken cancellationToken)
    {
        var entries = db.Shelves.AsNoTracking().Where(entry => entry.Owner == owner);

        var bookChanged = await entries
            .OrderByDescending(entry => entry.Book.UpdatedAt)
            .Select(entry => (DateTimeOffset?)entry.Book.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var shelved = await entries
            .OrderByDescending(entry => entry.AddedAt)
            .Select(entry => (DateTimeOffset?)entry.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var latest = bookChanged > shelved ? bookChanged : shelved ?? bookChanged;
        return latest ?? DateTimeOffset.UnixEpoch;
    }

    private static async Task<Page<Domain.Book>> PageAsync(
        IQueryable<BookEntity> query,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        page = Math.Max(0, page);
        size = Math.Clamp(size, 1, 200);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page * size).Take(size).ToListAsync(cancellationToken);

        return new Page<Domain.Book>([.. rows.Select(Map)], total, page, size);
    }

    /// <summary>The one place an EF row becomes a domain value.</summary>
    public static Domain.Book Map(BookEntity entity) =>
        new(
            entity.Id,
            entity.Title,
            string.IsNullOrEmpty(entity.SortTitle) ? entity.Title : entity.SortTitle,
            entity.Authors
                .OrderBy(link => link.Order)
                .Select(link => new Domain.Author(link.Author.Name, link.Author.SortName))
                .ToFSharpList(),
            entity.Summary.ToOption(),
            entity.Language.ToOption(),
            entity.Series is null
                ? Microsoft.FSharp.Core.FSharpOption<Domain.SeriesRef>.None
                : Microsoft.FSharp.Core.FSharpOption<Domain.SeriesRef>.Some(
                    new Domain.SeriesRef(entity.Series.Name, entity.SeriesIndex.ToValueOption())),
            entity.Tags.Select(link => link.Tag.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToFSharpList(),
            entity.Published.ToValueOption(),
            entity.AddedAt,
            entity.UpdatedAt,
            entity.SourceUrl.ToOption(),
            entity.SourcePlugin.ToOption(),
            entity.SourceId.ToOption(),
            entity.Chapters.ToValueOption(),
            !string.IsNullOrEmpty(entity.CoverPath),
            entity.Files
                .Select(file => new Domain.BookFile(
                    file.Format, file.ContentType, file.RelativePath, file.SizeBytes, file.Sha256))
                .ToFSharpList());

    /// <summary>LIKE wildcards in user input are literals, not operators.</summary>
    private static string Escape(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
