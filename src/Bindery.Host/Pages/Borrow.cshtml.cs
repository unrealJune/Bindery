using Bindery.Core;
using Bindery.Host.Circulation;
using Bindery.Host.Data;
using Bindery.Host.Security;
using Bindery.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

/// <summary>
/// Search every library the signed-in person has a card for, and place holds.
/// </summary>
/// <remarks>
/// The medium filter is the page's first control on purpose. A physical hold and a digital
/// one are different promises — a trip to a branch versus a licence in an app — and the
/// person should choose which they are looking for before they see a single result.
/// </remarks>
public sealed class BorrowModel(CirculationAccountStore accounts, CirculationService circulation) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    /// <summary>"any", "physical", or "digital".</summary>
    [BindProperty(SupportsGet = true, Name = "medium")]
    public string? MediumFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "card")]
    public Guid? Card { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public bool HasCirculationPlugins { get; private set; }

    public IReadOnlyList<CirculationAccountEntity> Accounts { get; private set; } = [];

    public IReadOnlyList<AccountSearchResult> Results { get; private set; } = [];

    public string Medium => MediumFilter is "physical" or "digital" ? MediumFilter : "any";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var owner = Identity.SubjectOf(User);

        HasCirculationPlugins = accounts.CirculationPlugins.Count > 0;
        Accounts = await accounts.ListAsync(owner, cancellationToken);

        if (string.IsNullOrWhiteSpace(Query) || Accounts.Count == 0)
        {
            return;
        }

        Results = await circulation.SearchAsync(
            owner,
            Query.Trim(),
            Protocol.MediumModule.ofWire(Medium).OrNull(),
            Card,
            Math.Max(1, PageNumber),
            cancellationToken);
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
