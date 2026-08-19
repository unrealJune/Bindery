using Bindery.Core;
using Bindery.Host.Catalog;
using Bindery.Host.Configuration;
using Bindery.Host.Library;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.FSharp.Core;
using CoreOpds = Bindery.Core.Opds;

namespace Bindery.Host.Opds;

/// <summary>
/// The OPDS surface: the same tree served as 1.2 Atom and as 2.0 JSON.
/// </summary>
/// <remarks>
/// Both versions are built from one feed model and rendered twice, so they cannot drift.
/// The endpoints here do routing and paging; everything about how a feed is shaped lives
/// in <c>Bindery.Core.Opds</c>, where it is unit-tested against reference documents.
/// </remarks>
public static class OpdsEndpoints
{
    public static IEndpointRouteBuilder MapOpds(this IEndpointRouteBuilder builder)
    {
        // OPDS 1.2 — what ereaders actually implement.
        var atom = builder.MapGroup(OpdsUrls.AtomPrefix)
            .RequireAuthorization(AuthPolicies.ReadCatalog)
            .WithTags("OPDS");

        // OPDS 2.0 — where things are going. Mapped first so "/opds/v2" is not swallowed
        // by the 1.2 group's routes.
        var json = builder.MapGroup(OpdsUrls.JsonPrefix)
            .RequireAuthorization(AuthPolicies.ReadCatalog)
            .WithTags("OPDS 2.0");

        MapTree(atom, OpdsUrls.Atom, json: false);
        MapTree(json, OpdsUrls.Json, json: true);

        atom.MapGet("/opensearch.xml", (IOptions<BinderyOptions> options) =>
        {
            var name = options.Value.CatalogTitle;

            var xml = OpdsAtom.renderOpenSearch(
                name,
                $"Search {name}",
                $"{OpdsUrls.AtomPrefix}/search?q={{searchTerms}}",
                $"{OpdsUrls.JsonPrefix}/search?q={{searchTerms}}");

            return Results.Text(xml, CoreOpds.ContentTypes.OpenSearch);
        });

        atom.MapGet("/download/{id:guid}.{format}", DownloadAsync);
        atom.MapGet("/cover/{id:guid}", (Guid id, CatalogService catalog, LibraryStore library, CancellationToken ct) =>
            ImageAsync(id, catalog, library, ct));
        atom.MapGet("/thumb/{id:guid}", (Guid id, CatalogService catalog, LibraryStore library, CancellationToken ct) =>
            ImageAsync(id, catalog, library, ct));

        return builder;
    }

    private static void MapTree(RouteGroupBuilder group, CoreOpds.UrlBuilder urls, bool json)
    {
        group.MapGet("/", async (CatalogService catalog, IOptions<BinderyOptions> options, CancellationToken ct) =>
        {
            var updated = await catalog.LastModifiedAsync(ct);
            var authors = await catalog.AuthorsAsync(ct);
            var series = await catalog.SeriesAsync(ct);
            var tags = await catalog.TagsAsync(ct);
            var total = await catalog.CountAsync(ct);

            var entries = new List<CoreOpds.Entry>
            {
                Nav(urls, "new", "Recently added", "The most recent arrivals", CoreOpds.FeedKind.Acquisition, total, updated),
                Nav(urls, "all", "All books", "Everything, by title", CoreOpds.FeedKind.Acquisition, total, updated),
                Nav(urls, "authors", "Authors", $"{authors.Count} author(s)", CoreOpds.FeedKind.Navigation, authors.Count, updated),
                Nav(urls, "series", "Series", $"{series.Count} series", CoreOpds.FeedKind.Navigation, series.Count, updated),
                Nav(urls, "tags", "Tags", $"{tags.Count} tag(s)", CoreOpds.FeedKind.Navigation, tags.Count, updated)
            };

            var feed = new CoreOpds.Feed(
                CoreOpds.Urn.feed("root"),
                options.Value.CatalogTitle,
                options.Value.CatalogSubtitle.ToOption(),
                updated,
                CoreOpds.FeedKind.Navigation,
                FSharpOption<string>.None,
                FSharpOption<CoreOpds.EntryAuthor>.None,
                CoreOpds.Build.standardLinks(urls, urls.Root, CoreOpds.FeedKind.Navigation, FSharpOption<string>.None),
                entries.ToFSharpList(),
                FSharpOption<CoreOpds.Paging>.None);

            return Render(feed, json);
        });

        group.MapGet("/new", (CatalogService catalog, IOptions<BinderyOptions> options, int? page, CancellationToken ct) =>
            AcquisitionAsync(
                catalog, options, urls, json, "new", "Recently added", page,
                (size, index) => catalog.RecentAsync(index, size, ct), ct));

        group.MapGet("/all", (CatalogService catalog, IOptions<BinderyOptions> options, int? page, CancellationToken ct) =>
            AcquisitionAsync(
                catalog, options, urls, json, "all", "All books", page,
                (size, index) => catalog.AllAsync(index, size, ct), ct));

        group.MapGet("/search", (CatalogService catalog, IOptions<BinderyOptions> options, [FromQuery] string? q, int? page, CancellationToken ct) =>
            AcquisitionAsync(
                catalog, options, urls, json,
                $"search?q={Uri.EscapeDataString(q ?? string.Empty)}",
                string.IsNullOrWhiteSpace(q) ? "Search" : $"Results for “{q}”",
                page,
                (size, index) => catalog.SearchAsync(q ?? string.Empty, index, size, ct),
                ct));

        group.MapGet("/authors", async (CatalogService catalog, CancellationToken ct) =>
        {
            var updated = await catalog.LastModifiedAsync(ct);
            var groupings = await catalog.AuthorsAsync(ct);
            return Render(GroupingFeed(urls, "authors", "Authors", groupings, updated), json);
        });

        group.MapGet("/authors/{id:int}", async (int id, CatalogService catalog, IOptions<BinderyOptions> options, int? page, CancellationToken ct) =>
        {
            var author = await catalog.FindAuthorAsync(id, ct);

            if (author is null)
            {
                return Results.NotFound();
            }

            return await AcquisitionAsync(
                catalog, options, urls, json, $"authors/{id}", author.Name, page,
                (size, index) => catalog.ByAuthorAsync(id, index, size, ct), ct, up: "authors");
        });

        group.MapGet("/series", async (CatalogService catalog, CancellationToken ct) =>
        {
            var updated = await catalog.LastModifiedAsync(ct);
            var groupings = await catalog.SeriesAsync(ct);
            return Render(GroupingFeed(urls, "series", "Series", groupings, updated), json);
        });

        group.MapGet("/series/{id:int}", async (int id, CatalogService catalog, IOptions<BinderyOptions> options, int? page, CancellationToken ct) =>
        {
            var series = await catalog.FindSeriesAsync(id, ct);

            if (series is null)
            {
                return Results.NotFound();
            }

            return await AcquisitionAsync(
                catalog, options, urls, json, $"series/{id}", series.Name, page,
                (size, index) => catalog.BySeriesAsync(id, index, size, ct), ct, up: "series");
        });

        group.MapGet("/tags", async (CatalogService catalog, CancellationToken ct) =>
        {
            var updated = await catalog.LastModifiedAsync(ct);
            var groupings = await catalog.TagsAsync(ct);
            return Render(GroupingFeed(urls, "tags", "Tags", groupings, updated), json);
        });

        group.MapGet("/tags/{id:int}", async (int id, CatalogService catalog, IOptions<BinderyOptions> options, int? page, CancellationToken ct) =>
        {
            var tag = await catalog.FindTagAsync(id, ct);

            if (tag is null)
            {
                return Results.NotFound();
            }

            return await AcquisitionAsync(
                catalog, options, urls, json, $"tags/{id}", tag.Name, page,
                (size, index) => catalog.ByTagAsync(id, index, size, ct), ct, up: "tags");
        });

        group.MapGet("/book/{id:guid}", async (Guid id, CatalogService catalog, CancellationToken ct) =>
        {
            var book = await catalog.FindAsync(id, ct);

            if (book is null)
            {
                return Results.NotFound();
            }

            var entry = CoreOpds.Build.bookEntry(urls, book);

            return json
                ? Results.Text(OpdsJson.render(SingleEntryFeed(urls, entry, book.Updated)), CoreOpds.ContentTypes.Opds2)
                : Results.Text(OpdsAtom.renderEntry(entry), CoreOpds.ContentTypes.Entry);
        });
    }

    // ------------------------------------------------------------ feeds

    private static async Task<IResult> AcquisitionAsync(
        CatalogService catalog,
        IOptions<BinderyOptions> options,
        CoreOpds.UrlBuilder urls,
        bool json,
        string path,
        string title,
        int? page,
        Func<int, int, Task<Page<Domain.Book>>> query,
        CancellationToken cancellationToken,
        string? up = null)
    {
        var size = options.Value.PageSize;
        var result = await query(size, Math.Max(0, page ?? 0));
        var updated = await catalog.LastModifiedAsync(cancellationToken);

        var self = $"{urls.Root}/{path}";
        var separator = path.Contains('?') ? "&" : "?";

        var paging = new CoreOpds.Paging(result.Total, result.PageSize, result.StartIndex);

        var links = CoreOpds.Build
            .standardLinks(urls, self, CoreOpds.FeedKind.Acquisition, (up is null ? null : $"{urls.Root}/{up}").ToOption())
            .AsList()
            .Concat(CoreOpds.Build
                .pagingLinks(
                    paging,
                    json ? CoreOpds.ContentTypes.Opds2 : CoreOpds.ContentTypes.Acquisition,
                    FuncConvert.FromFunc<int, string>(index => $"{self}{separator}page={index}"))
                .AsList())
            .ToFSharpList();

        var feed = new CoreOpds.Feed(
            CoreOpds.Urn.feed(path),
            title,
            FSharpOption<string>.None,
            updated,
            CoreOpds.FeedKind.Acquisition,
            FSharpOption<string>.None,
            FSharpOption<CoreOpds.EntryAuthor>.None,
            links,
            result.Items.Select(book => CoreOpds.Build.bookEntry(urls, book)).ToFSharpList(),
            FSharpOption<CoreOpds.Paging>.Some(paging));

        return Render(feed, json);
    }

    private static CoreOpds.Feed GroupingFeed(
        CoreOpds.UrlBuilder urls,
        string path,
        string title,
        IReadOnlyList<Domain.Grouping> groupings,
        DateTimeOffset updated) =>
        new(
            CoreOpds.Urn.feed(path),
            title,
            FSharpOption<string>.None,
            updated,
            CoreOpds.FeedKind.Navigation,
            FSharpOption<string>.None,
            FSharpOption<CoreOpds.EntryAuthor>.None,
            CoreOpds.Build.standardLinks(urls, $"{urls.Root}/{path}", CoreOpds.FeedKind.Navigation, urls.Root.ToOption()),
            groupings
                .Select(grouping => CoreOpds.Build.navigationEntry(
                    CoreOpds.Urn.grouping(path, grouping.Key),
                    grouping.Label,
                    $"{urls.Root}/{path}/{grouping.Key}",
                    CoreOpds.FeedKind.Acquisition,
                    $"{grouping.Count} book{(grouping.Count == 1 ? "" : "s")}".ToOption(),
                    grouping.Count.ToValueOption(),
                    updated))
                .ToFSharpList(),
            FSharpOption<CoreOpds.Paging>.None);

    private static CoreOpds.Feed SingleEntryFeed(CoreOpds.UrlBuilder urls, CoreOpds.Entry entry, DateTimeOffset updated) =>
        new(
            entry.Id,
            entry.Title,
            FSharpOption<string>.None,
            updated,
            CoreOpds.FeedKind.Acquisition,
            FSharpOption<string>.None,
            FSharpOption<CoreOpds.EntryAuthor>.None,
            CoreOpds.Build.standardLinks(urls, urls.Root, CoreOpds.FeedKind.Acquisition, FSharpOption<string>.None),
            new[] { entry }.ToFSharpList(),
            FSharpOption<CoreOpds.Paging>.None);

    private static CoreOpds.Entry Nav(
        CoreOpds.UrlBuilder urls,
        string path,
        string title,
        string subtitle,
        CoreOpds.FeedKind kind,
        int count,
        DateTimeOffset updated) =>
        CoreOpds.Build.navigationEntry(
            CoreOpds.Urn.feed(path),
            title,
            $"{urls.Root}/{path}",
            kind,
            subtitle.ToOption(),
            count.ToValueOption(),
            updated);

    private static IResult Render(CoreOpds.Feed feed, bool json) =>
        json
            ? Results.Text(OpdsJson.render(feed), CoreOpds.ContentTypes.Opds2)
            : Results.Text(OpdsAtom.render(feed), feed.Kind.ContentType);

    // ------------------------------------------------------------ files

    private static async Task<IResult> DownloadAsync(
        Guid id,
        string format,
        CatalogService catalog,
        LibraryStore library,
        CancellationToken cancellationToken)
    {
        var book = await catalog.FindAsync(id, cancellationToken);

        if (book is null)
        {
            return Results.NotFound();
        }

        var file = book.Files.AsList()
            .FirstOrDefault(candidate => string.Equals(candidate.Format, format, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            return Results.NotFound();
        }

        var info = library.FileInfoFor(file.RelativePath);

        if (info is null)
        {
            // The index outlived the file. Say so plainly rather than serving a zero-byte
            // download that a reader will cache as a broken book.
            return Results.NotFound();
        }

        var name = Domain.Naming.fileName(book.Title, book.AuthorLine, file.Format);

        return Results.File(
            info.FullName,
            file.ContentType,
            name,
            lastModified: info.LastWriteTimeUtc,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file.Sha256[..16]}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> ImageAsync(
        Guid id,
        CatalogService catalog,
        LibraryStore library,
        CancellationToken cancellationToken)
    {
        var entity = await catalog.FindEntityAsync(id, cancellationToken);

        if (entity?.CoverPath is null)
        {
            return Results.NotFound();
        }

        var info = library.FileInfoFor(entity.CoverPath);

        if (info is null)
        {
            return Results.NotFound();
        }

        // Thumbnails are the cover as stored. Resizing would mean an image library in the
        // host image for a saving readers do not notice on covers this small.
        return Results.File(
            info.FullName,
            Domain.Formats.imageContentType(Path.GetExtension(entity.CoverPath)),
            lastModified: info.LastWriteTimeUtc,
            entityTag: null,
            enableRangeProcessing: false);
    }
}
