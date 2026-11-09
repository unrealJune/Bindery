using System.ComponentModel.DataAnnotations;
using Bindery.Core;
using Bindery.Host.Catalog;
using Bindery.Host.Circulation;
using Bindery.Host.Data;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Bindery.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

/// <summary>
/// Search the person's shelf, library catalogs, and opt-in downloader actions together.
/// </summary>
public sealed class BorrowModel(
    CirculationAccountStore accounts,
    CirculationService circulation,
    CatalogService catalog,
    PluginActionService actions) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    [StringLength(500)]
    public string? Query { get; set; }

    /// <summary>"any", "physical", or "digital".</summary>
    [BindProperty(SupportsGet = true, Name = "medium")]
    public string? MediumFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "card")]
    public Guid? Card { get; set; }

    [FromQuery(Name = "page")]
    public int PageNumber { get; set; } = 1;

    // Razor reserves "page" for its route. Read old bookmarks, but generate catalogPage.
    [BindProperty(SupportsGet = true, Name = "catalogPage")]
    public int? CatalogPageNumber { get; set; }

    [BindProperty(SupportsGet = true, Name = "shelfPage")]
    public int ShelfPage { get; set; }

    public Page<Domain.Book> ShelfBooks { get; private set; } = new([], 0, 0, 12);

    public IReadOnlyList<PluginSearchResult> PluginResults { get; private set; } = [];

    public bool HasCirculationPlugins { get; private set; }

    public IReadOnlyList<CirculationAccountEntity> Accounts { get; private set; } = [];

    public IReadOnlyList<AccountSearchResult> Results { get; private set; } = [];

    public string Medium => MediumFilter is "physical" or "digital" ? MediumFilter : "any";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var owner = Identity.SubjectOf(User);

        HasCirculationPlugins = accounts.CirculationPlugins.Count > 0;
        Accounts = await accounts.ListAsync(owner, cancellationToken);

        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(Query))
        {
            return;
        }

        Query = Query.Trim();
        PageNumber = Math.Max(1, CatalogPageNumber ?? PageNumber);
        ShelfPage = Math.Max(0, ShelfPage);
        ShelfBooks = await catalog.SearchAsync(owner, Query, ShelfPage, 12, cancellationToken);
        Results = await circulation.SearchAsync(
            owner,
            Query,
            Protocol.MediumModule.ofWire(Medium).OrNull(),
            Card,
            PageNumber,
            cancellationToken);
        PluginResults = await actions.SearchAsync(Query, Person.Of(User), cancellationToken);
    }

    public async Task<IActionResult> OnPostHoldAsync(
        Guid account,
        string item,
        string? pickup,
        string? returnQuery,
        string? returnMedium,
        CancellationToken cancellationToken)
    {
        var result = await circulation.PlaceHoldAsync(
            Identity.SubjectOf(User), account, item, string.IsNullOrWhiteSpace(pickup) ? null : pickup, cancellationToken);

        TempData["Notice"] = result.Message;

        return result.Succeeded
            ? RedirectToPage("/Holds")
            : RedirectToPage(new { q = returnQuery, medium = returnMedium });
    }

    /// <summary>"Digital · eBook via OverDrive" — the medium, the format, and the platform.</summary>
    public static MediumBadgeView BadgeFor(Core.Circulation.CatalogItem item) =>
        new(
            item.Medium.IsDigital,
            item.Provider.OrNull() is { } provider ? $"{item.FormatLabel} via {provider}" : item.FormatLabel);

    /// <summary>"Herbert, Frank · 2019", leaving out whichever half the record lacks.</summary>
    public static string Byline(Core.Circulation.CatalogItem item) =>
        string.Join(" · ", new[] { string.Join("; ", item.Authors.AsList()), item.Year.OrNull() }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    public IReadOnlyList<PickupLocationView> PickupsFor(CirculationAccountEntity account) =>
        CirculationAccountStore.PickupLocations(account);
}
