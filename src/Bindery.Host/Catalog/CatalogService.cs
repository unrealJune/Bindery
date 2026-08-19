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
/// Everything here returns <c>Bindery.Core.Domain</c> types rather than EF entities, so
/// that feed generation is a pure function of data and the OPDS serializers never see a
/// tracked entity or a lazy load.
/// </remarks>
public sealed class CatalogService(BinderyDbContext db)
{
    private IQueryable<BookEntity> Books =>
        db.Books
            .AsNoTracking()
            .Include(book => book.Authors).ThenInclude(link => link.Author)
            .Include(book => book.Tags).ThenInclude(link => link.Tag)
            .Include(book => book.Files)
            .Include(book => book.Series);

    public async Task<Page<Domain.Book>> RecentAsync(int page, int size, CancellationToken cancellationToken) =>
        await PageAsync(Books.OrderByDescending(book => book.AddedAt), page, size, cancellationToken);

    public async Task<Page<Domain.Book>> AllAsync(int page, int size, CancellationToken cancellationToken) =>
        await PageAsync(Books.OrderBy(book => book.SortTitle), page, size, cancellationToken);

    public async Task<Page<Domain.Book>> SearchAsync(
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
        var matches = Books.Where(book =>
            EF.Functions.Like(book.Title, pattern)
            || (book.Summary != null && EF.Functions.Like(book.Summary, pattern))
            || book.Authors.Any(link => EF.Functions.Like(link.Author.Name, pattern))
            || book.Tags.Any(link => EF.Functions.Like(link.Tag.Name, pattern))
            || (book.Series != null && EF.Functions.Like(book.Series.Name, pattern)));

        return await PageAsync(matches.OrderBy(book => book.SortTitle), page, size, cancellationToken);
    }

    public async Task<Page<Domain.Book>> ByAuthorAsync(
        int authorId,
        int page,
        int size,
        CancellationToken cancellationToken) =>
        await PageAsync(
            Books.Where(book => book.Authors.Any(link => link.AuthorId == authorId))
                 .OrderBy(book => book.SortTitle),
            page,
            size,
            cancellationToken);

    public async Task<Page<Domain.Book>> ByTagAsync(
        int tagId,
        int page,
        int size,
        CancellationToken cancellationToken) =>
        await PageAsync(
            Books.Where(book => book.Tags.Any(link => link.TagId == tagId)).OrderBy(book => book.SortTitle),
            page,
            size,
            cancellationToken);

    public async Task<Page<Domain.Book>> BySeriesAsync(
        int seriesId,
        int page,
        int size,
        CancellationToken cancellationToken) =>
        await PageAsync(
            Books.Where(book => book.SeriesId == seriesId)
                 .OrderBy(book => book.SeriesIndex ?? double.MaxValue)
                 .ThenBy(book => book.SortTitle),
            page,
            size,
            cancellationToken);

    public async Task<Domain.Book?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await Books.FirstOrDefaultAsync(book => book.Id == id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public Task<BookEntity?> FindEntityAsync(Guid id, CancellationToken cancellationToken) =>
        Books.FirstOrDefaultAsync(book => book.Id == id, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) => db.Books.CountAsync(cancellationToken);

    public async Task<IReadOnlyList<Domain.Grouping>> AuthorsAsync(CancellationToken cancellationToken) =>
        await db.Authors
            .AsNoTracking()
            .Where(author => author.Books.Count > 0)
            .OrderBy(author => author.SortName)
            .Select(author => new { author.Id, author.Name, author.SortName, Count = author.Books.Count })
            .Select(author => new Domain.Grouping(
                author.Id.ToString(), author.Name, author.SortName, author.Count))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Domain.Grouping>> TagsAsync(CancellationToken cancellationToken) =>
        await db.Tags
            .AsNoTracking()
            .Where(tag => tag.Books.Count > 0)
            .OrderBy(tag => tag.Name)
            .Select(tag => new Domain.Grouping(tag.Id.ToString(), tag.Name, tag.Name, tag.Books.Count))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Domain.Grouping>> SeriesAsync(CancellationToken cancellationToken) =>
        await db.Series
            .AsNoTracking()
            .Where(series => series.Books.Count > 0)
            .OrderBy(series => series.Name)
            .Select(series => new Domain.Grouping(
                series.Id.ToString(), series.Name, series.Name, series.Books.Count))
            .ToListAsync(cancellationToken);

    public Task<AuthorEntity?> FindAuthorAsync(int id, CancellationToken cancellationToken) =>
        db.Authors.AsNoTracking().FirstOrDefaultAsync(author => author.Id == id, cancellationToken);

    public Task<TagEntity?> FindTagAsync(int id, CancellationToken cancellationToken) =>
        db.Tags.AsNoTracking().FirstOrDefaultAsync(tag => tag.Id == id, cancellationToken);

    public Task<SeriesEntity?> FindSeriesAsync(int id, CancellationToken cancellationToken) =>
        db.Series.AsNoTracking().FirstOrDefaultAsync(series => series.Id == id, cancellationToken);

    /// <summary>The most recent change to anything, used as a feed's <c>updated</c>.</summary>
    public async Task<DateTimeOffset> LastModifiedAsync(CancellationToken cancellationToken)
    {
        var latest = await db.Books
            .AsNoTracking()
            .OrderByDescending(book => book.UpdatedAt)
            .Select(book => (DateTimeOffset?)book.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

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
