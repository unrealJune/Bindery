using Bindery.Core;
using Bindery.Host.Circulation;
using Bindery.Host.Data;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

public sealed record HoldLane(string Key, string Title, string Hint, IReadOnlyList<CirculationItemEntity> Items);

/// <summary>
/// The person's holds and loans across every library card, grouped by what they need to do.
/// </summary>
/// <remarks>
/// Lanes are ordered by urgency, not by lifecycle: what is waiting on a shelf with a deadline
/// comes first, because missing it is the failure this whole feature exists to prevent.
/// </remarks>
public sealed class HoldsModel(CirculationAccountStore accounts, CirculationService circulation) : PageModel
{
    public IReadOnlyList<CirculationAccountEntity> Accounts { get; private set; } = [];

    public IReadOnlyList<HoldLane> Lanes { get; private set; } = [];

    public IReadOnlyList<CirculationItemEntity> History { get; private set; } = [];

    public int ActiveCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var owner = Identity.SubjectOf(User);

        Accounts = await accounts.ListAsync(owner, cancellationToken);
        var active = await circulation.ActiveAsync(owner, cancellationToken);
        History = await circulation.HistoryAsync(owner, cancellationToken);
        ActiveCount = active.Count;

        IReadOnlyList<CirculationItemEntity> In(params string[] states) =>
            [.. active.Where(item => states.Contains(item.State))];

        Lanes =
        [
            new("ready", "Ready for you", "Waiting on a shelf, or ready to borrow in the app.", In("ready")),
            new("overdue", "Overdue", "Past the due date.", In("overdue")),
            new("in_transit", "On the way", "A copy is assigned and travelling to your branch.", In("in_transit")),
            new("checked_out", "Checked out", "Currently with you.", In("checked_out")),
            new("pending", "In the queue", "Waiting for a copy to come free.",
                [.. In("pending").OrderBy(item => item.QueuePosition ?? int.MaxValue)]),
            new("suspended", "Paused", "Keeping their place, skipped until resumed.", In("suspended")),
        ];
    }

    public async Task<IActionResult> OnPostSyncAsync(CancellationToken cancellationToken)
    {
        var owner = Identity.SubjectOf(User);
        var problems = new List<string>();

        foreach (var account in await accounts.ListAsync(owner, cancellationToken))
        {
            var result = await circulation.SyncAsync(account, cancellationToken);

            if (!result.Succeeded)
            {
                problems.Add($"{account.LibraryName}: {result.Problem}");
            }
        }

        TempData["Notice"] = problems.Count == 0 ? "Up to date with every library." : string.Join(" ", problems);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostOperateAsync(
        Guid item,
        string operation,
        DateTimeOffset? until,
        CancellationToken cancellationToken)
    {
        var op = Protocol.HoldOperationModule.ofWire(operation ?? string.Empty).OrNull();

        if (op is null)
        {
            return BadRequest();
        }

        var result = await circulation.OperateAsync(Identity.SubjectOf(User), item, op, until, cancellationToken);
        TempData["Notice"] = result.Message;
        return RedirectToPage();
    }

    /// <summary>What the person can do to this hold, given what its plugin supports.</summary>
    public IReadOnlyList<string> OperationsFor(CirculationItemEntity item)
    {
        if (item.Kind != "hold")
        {
            return [];
        }

        var spec = accounts.Resolve(item.Account.Plugin).Spec;

        if (spec is null)
        {
            return [];
        }

        var supported = spec.HoldOperations.AsList().Select(operation => operation.Wire).ToHashSet();
        var offered = new List<string>();

        if (item.State == "suspended" && supported.Contains("resume"))
        {
            offered.Add("resume");
        }

        if (item.State == "pending" && supported.Contains("suspend"))
        {
            offered.Add("suspend");
        }

        if (supported.Contains("cancel"))
        {
            offered.Add("cancel");
        }

        return offered;
    }
}
