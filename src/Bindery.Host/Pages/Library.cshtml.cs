using Bindery.Core;
using Bindery.Host.Catalog;
using Bindery.Host.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

public sealed class LibraryModel(CatalogService catalog, LibraryScanner scanner) : PageModel
{
    [FromQuery(Name = "q")]
    public string? Query { get; set; }

    [FromQuery(Name = "page")]
    public int PageIndex { get; set; }

    public Page<Domain.Book> Books { get; private set; } = new([], 0, 0, 24);

    public IReadOnlyList<Domain.Grouping> Authors { get; private set; } = [];

    public IReadOnlyList<Domain.Grouping> SeriesGroups { get; private set; } = [];

    public IReadOnlyList<Domain.Grouping> Tags { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostScanAsync(CancellationToken cancellationToken)
    {
        var report = await scanner.ScanAsync(cancellationToken);

        TempData["Notice"] =
            $"Rescan complete: {report.Added} added, {report.Updated} updated, {report.Removed} removed, " +
            $"{report.Skipped} skipped.";

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        PageIndex = Math.Max(0, PageIndex);

        Books = string.IsNullOrWhiteSpace(Query)
            ? await catalog.AllAsync(PageIndex, 24, cancellationToken)
            : await catalog.SearchAsync(Query, PageIndex, 24, cancellationToken);

        Authors = await catalog.AuthorsAsync(cancellationToken);
        SeriesGroups = await catalog.SeriesAsync(cancellationToken);
        Tags = await catalog.TagsAsync(cancellationToken);
    }
}
