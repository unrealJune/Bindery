using Bindery.Core;
using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Bindery.Host.Notifications;
using Bindery.Host.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.FSharp.Core;
using CirculationCore = Bindery.Core.Circulation;

namespace Bindery.Host.Circulation;

public sealed record AccountSearchResult(
    CirculationAccountEntity Account,
    CirculationCore.SearchPage? Page,
    string? Problem);

public sealed record CirculationActionResult(bool Succeeded, string Message);

public sealed record SyncResult(bool Succeeded, string? Problem, int Events);

/// <summary>
/// Search, holds, and the lifecycle of everything on a person's library accounts.
/// </summary>
/// <remarks>
/// <para>
/// The rules — what a disappearance means, when a reminder is due — are
/// <see cref="CirculationCore.reconcile"/>, a pure function in the core. This class is its
/// plumbing: it loads what the host knew, asks the plugin what is true now, hands both to
/// the function, and writes back what it concluded, including the history and any
/// notification.
/// </para>
/// <para>
/// Every public method takes the owner. Nothing here acts on an account or an item without
/// first proving it belongs to the person asking.
/// </para>
/// </remarks>
public sealed class CirculationService(
    BinderyDbContext db,
    CirculationAccountStore accounts,
    PluginClient client,
    NotificationService notifications,
    IOptions<BinderyOptions> options,
    ILogger<CirculationService> logger)
{
    private static readonly string[] ActiveStates =
        [.. CirculationCore.TrackedStateModule.all.Where(state => state.IsActive).Select(state => state.Wire)];

    private readonly CirculationOptions _options = options.Value.Circulation;

    // ------------------------------------------------------------ search

    /// <summary>
    /// Searches every library the person has a card for (or just one), in parallel. A library
    /// that fails is reported in its own section rather than failing the whole search.
    /// </summary>
    public async Task<IReadOnlyList<AccountSearchResult>> SearchAsync(
        string owner,
        string query,
        Protocol.Medium? medium,
        Guid? onlyAccount,
        int page,
        CancellationToken cancellationToken)
    {
        var mine = (await accounts.ListAsync(owner, cancellationToken))
            .Where(account => onlyAccount is null || account.Id == onlyAccount)
            .ToList();

        // Protocol objects and descriptors are built on this thread; only the plugin calls,
        // which touch no DbContext, run concurrently.
        var calls = mine.Select(account => (Account: account, Descriptor: accounts.Resolve(account.Plugin).Descriptor,
                Protocol: accounts.ToProtocol(account)))
            .Select(async call =>
            {
                if (call.Descriptor is null)
                {
                    return new AccountSearchResult(call.Account, null, "The plugin for this card is not available right now.");
                }

                try
                {
                    var reply = await client.SearchCatalogAsync(
                        call.Descriptor.Entry, call.Protocol, query, medium, page, cancellationToken);

                    return reply switch
                    {
                        CirculationCore.Reply<CirculationCore.SearchPage>.Answered answered =>
                            new AccountSearchResult(call.Account, answered.Item, null),
                        CirculationCore.Reply<CirculationCore.SearchPage>.Refused refused =>
                            new AccountSearchResult(call.Account, null, refused.Item.Message),
                        _ => new AccountSearchResult(call.Account, null, "Unrecognizable answer.")
                    };
                }
                catch (Exception ex) when (IsTransport(ex, cancellationToken))
                {
                    logger.LogWarning(ex, "search on {Plugin} failed", call.Account.Plugin);
                    return new AccountSearchResult(call.Account, null, "The library did not answer. Try again in a moment.");
                }
            });

        return await Task.WhenAll(calls);
    }

    // ------------------------------------------------------------ holds

    public async Task<CirculationActionResult> PlaceHoldAsync(
        string owner,
        Guid accountId,
        string itemId,
        string? pickupLocationId,
        CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(owner, accountId, cancellationToken);
        var descriptor = account is null ? null : accounts.Resolve(account.Plugin).Descriptor;

        if (account is null || descriptor is null)
        {
            return new CirculationActionResult(false, "That library card is not available.");
        }

        if (!string.IsNullOrEmpty(pickupLocationId)
            && CirculationAccountStore.PickupLocations(account).All(location => location.Id != pickupLocationId))
        {
            return new CirculationActionResult(false, "That pickup branch is not one this library offers.");
        }

        try
        {
            var reply = await client.PlaceHoldAsync(
                descriptor.Entry, accounts.ToProtocol(account), itemId, pickupLocationId, cancellationToken);

            if (reply is CirculationCore.Reply<FSharpOption<CirculationCore.ObservedHold>>.Refused refused)
            {
                return new CirculationActionResult(false, Explain(refused.Item));
            }
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            logger.LogWarning(ex, "placing a hold on {Plugin} failed", account.Plugin);
            return new CirculationActionResult(false, "The library did not answer, so the hold may not have been placed.");
        }

        // The snapshot, not the reply, is what the host believes — sync now so the board shows it.
        await SyncAsync(account, cancellationToken);
        return new CirculationActionResult(true, $"Hold placed at {account.LibraryName}.");
    }

    /// <summary>Cancels, pauses, or resumes one of the person's holds.</summary>
    public async Task<CirculationActionResult> OperateAsync(
        string owner,
        Guid itemId,
        Protocol.HoldOperation operation,
        DateTimeOffset? until,
        CancellationToken cancellationToken)
    {
        var item = await db.CirculationItems
            .Include(candidate => candidate.Account)
            .FirstOrDefaultAsync(candidate => candidate.Id == itemId && candidate.Account.Owner == owner, cancellationToken);

        if (item is null || item.Kind != CirculationCore.TrackedKind.HoldKind.Wire || !ActiveStates.Contains(item.State))
        {
            return new CirculationActionResult(false, "That hold is not active on any of your cards.");
        }

        var (descriptor, spec) = accounts.Resolve(item.Account.Plugin);

        if (descriptor is null || spec is null || !spec.HoldOperations.Contains(operation))
        {
            return new CirculationActionResult(false, "This library does not support that through Bindery.");
        }

        try
        {
            var reply = await client.HoldOperationAsync(
                descriptor.Entry, accounts.ToProtocol(item.Account), item.ExternalId, operation, until, cancellationToken);

            if (reply is CirculationCore.Reply<FSharpOption<CirculationCore.ObservedHold>>.Refused refused)
            {
                return new CirculationActionResult(false, Explain(refused.Item));
            }
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            logger.LogWarning(ex, "hold {Operation} on {Plugin} failed", operation.Wire, item.Account.Plugin);
            return new CirculationActionResult(false, "The library did not answer. Nothing was changed on this side.");
        }

        if (operation.IsCancelHold)
        {
            // Recorded now, so the next snapshot's absence is not read as the library closing it.
            Persist(item.Account, CirculationCore.cancel(ToTracked(item)), item, observedHold: null, observedLoan: null);
            await db.SaveChangesAsync(cancellationToken);
            return new CirculationActionResult(true, $"Cancelled your hold on {item.Title}.");
        }

        await SyncAsync(item.Account, cancellationToken);

        return new CirculationActionResult(true, operation.IsSuspendHold
            ? $"Paused your hold on {item.Title}."
            : $"Resumed your hold on {item.Title}.");
    }

    // ------------------------------------------------------------ sync

    public async Task<SyncResult> SyncAsync(string owner, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(owner, accountId, cancellationToken);
        return account is null ? new SyncResult(false, "That library card is not connected.", 0) : await SyncAsync(account, cancellationToken);
    }

    /// <summary>
    /// Takes a snapshot of one account and reconciles it. A failed snapshot changes nothing
    /// but the account's error: drawing conclusions from an incomplete picture is exactly
    /// what the protocol forbids.
    /// </summary>
    public async Task<SyncResult> SyncAsync(CirculationAccountEntity account, CancellationToken cancellationToken)
    {
        var descriptor = accounts.Resolve(account.Plugin).Descriptor;
        account.LastAttemptAt = DateTimeOffset.UtcNow;

        if (descriptor is null)
        {
            return await FailSyncAsync(account, "The plugin for this card is not available right now.", false, cancellationToken);
        }

        CirculationCore.Snapshot snapshot;

        try
        {
            var reply = await client.SnapshotAsync(descriptor.Entry, accounts.ToProtocol(account), cancellationToken);

            switch (reply)
            {
                case CirculationCore.Reply<CirculationCore.Snapshot>.Answered answered:
                    snapshot = answered.Item;
                    break;
                case CirculationCore.Reply<CirculationCore.Snapshot>.Refused refused:
                    return await FailSyncAsync(
                        account, Explain(refused.Item), refused.Item.Code.IsAuthRequired, cancellationToken);
                default:
                    return await FailSyncAsync(account, "Unrecognizable answer.", false, cancellationToken);
            }
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            logger.LogWarning(ex, "snapshot of a {Plugin} account failed", account.Plugin);
            return await FailSyncAsync(account, "The library did not answer.", false, cancellationToken);
        }

        var rows = await db.CirculationItems
            .Where(item => item.AccountId == account.Id && ActiveStates.Contains(item.State))
            .ToListAsync(cancellationToken);

        var settings = new CirculationCore.Settings(_options.PickupReminder, _options.DueReminder);
        var firstSync = account.LastSyncedAt is null;

        var outcomes = CirculationCore.reconcile(
            settings,
            DateTimeOffset.UtcNow,
            firstSync,
            rows.Select(ToTracked).ToFSharpList(),
            snapshot);

        var holds = snapshot.Holds.AsList().ToDictionary(hold => hold.Id, StringComparer.Ordinal);
        var loans = snapshot.Loans.AsList().ToDictionary(loan => loan.Id, StringComparer.Ordinal);
        var notices = new List<(CirculationItemEntity Item, CirculationCore.Event Event)>();

        foreach (var outcome in outcomes.AsList())
        {
            var after = outcome.After;
            var row = rows.FirstOrDefault(candidate =>
                candidate.ExternalId == after.Key && candidate.Kind == after.Kind.Wire);

            var persisted = Persist(
                account,
                outcome,
                row,
                after.Kind.IsHoldKind ? holds.GetValueOrDefault(after.Key) : null,
                after.Kind.IsLoanKind ? loans.GetValueOrDefault(after.Key) : null);

            notices.AddRange(outcome.Events.AsList()
                .Where(@event => @event.Category.HasValue())
                .Select(@event => (persisted, @event)));
        }

        account.LastSyncedAt = DateTimeOffset.UtcNow;
        account.LastError = null;
        account.NeedsAttention = false;
        await db.SaveChangesAsync(cancellationToken);

        foreach (var (item, @event) in notices)
        {
            var message = CirculationMessages.Compose(account, item, @event, options.Value);
            await notifications.EnqueueAsync(
                account.Owner, @event.Category.Value, $"circulation:{account.Plugin}", message.Subject, message.Body, cancellationToken);
        }

        var total = outcomes.AsList().Sum(outcome => outcome.Events.Length);
        logger.LogInformation(
            "synced a {Plugin} account: {Holds} hold(s), {Loans} loan(s), {Events} event(s)",
            account.Plugin, holds.Count, loans.Count, total);

        return new SyncResult(true, null, total);
    }

    // ------------------------------------------------------------ the board

    public async Task<IReadOnlyList<CirculationItemEntity>> ActiveAsync(string owner, CancellationToken cancellationToken) =>
        await db.CirculationItems.AsNoTracking()
            .Include(item => item.Account)
            .Where(item => item.Account.Owner == owner && ActiveStates.Contains(item.State))
            .OrderBy(item => item.ReadyUntil ?? item.DueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Title)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CirculationItemEntity>> HistoryAsync(string owner, CancellationToken cancellationToken) =>
        await db.CirculationItems.AsNoTracking()
            .Include(item => item.Account)
            .Include(item => item.Events.OrderByDescending(e => e.At).Take(1))
            .Where(item => item.Account.Owner == owner && !ActiveStates.Contains(item.State))
            .OrderByDescending(item => item.StateChangedAt)
            .Take(Math.Max(1, _options.HistoryLimit))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CirculationEventEntity>> EventsAsync(
        string owner,
        Guid itemId,
        CancellationToken cancellationToken) =>
        await db.CirculationEvents.AsNoTracking()
            .Where(entry => entry.ItemId == itemId && entry.Item.Account.Owner == owner)
            .OrderByDescending(entry => entry.At)
            .ToListAsync(cancellationToken);

    // ------------------------------------------------------------ mapping

    private CirculationItemEntity Persist(
        CirculationAccountEntity account,
        CirculationCore.Outcome outcome,
        CirculationItemEntity? row,
        CirculationCore.ObservedHold? observedHold,
        CirculationCore.ObservedLoan? observedLoan)
    {
        var after = outcome.After;
        var now = DateTimeOffset.UtcNow;

        if (row is null)
        {
            row = new CirculationItemEntity
            {
                AccountId = account.Id,
                Kind = after.Kind.Wire,
                ExternalId = Clip(after.Key, 200)!,
                FirstSeenAt = now,
                StateChangedAt = now
            };

            db.CirculationItems.Add(row);
        }

        var previous = row.State;

        if (row.State != after.State.Wire)
        {
            row.StateChangedAt = now;
        }

        row.State = after.State.Wire;
        row.CatalogId = Clip(after.ItemId.OrNull(), 200);
        row.Title = Clip(after.Title, 500)!;
        row.Medium = after.Medium.Wire;
        row.QueuePosition = after.QueuePosition.OrNullable();
        row.QueueSize = after.QueueSize.OrNullable();
        row.PickupLocation = Clip(after.PickupLocation.OrNull(), 200);
        row.ReadyUntil = after.ReadyUntil.OrNullable();
        row.SuspendedUntil = after.SuspendedUntil.OrNullable();
        row.DueAt = after.DueAt.OrNullable();
        row.RemindedFor = after.RemindedFor.OrNullable();

        if (observedHold is not null)
        {
            row.LastSeenAt = now;
            row.Authors = Clip(string.Join("; ", observedHold.Authors.AsList()), 500);
            row.Format = Clip(observedHold.Format.OrNull(), 32);
            row.CoverUrl = HttpOnly(observedHold.CoverUrl.OrNull());
            row.Url = HttpOnly(observedHold.Url.OrNull());
            row.PlacedAt = observedHold.PlacedAt.OrNullable() ?? row.PlacedAt;
        }
        else if (observedLoan is not null)
        {
            row.LastSeenAt = now;
            row.Authors = Clip(string.Join("; ", observedLoan.Authors.AsList()), 500);
            row.Format = Clip(observedLoan.Format.OrNull(), 32);
            row.CoverUrl = HttpOnly(observedLoan.CoverUrl.OrNull());
            row.Url = HttpOnly(observedLoan.Url.OrNull());
        }

        var medium = after.Medium;

        foreach (var @event in outcome.Events.AsList())
        {
            row.Events.Add(new CirculationEventEntity
            {
                At = now,
                Kind = @event.Wire,
                FromState = outcome.Before is null ? null : previous,
                ToState = after.State.Wire,
                Detail = Clip(CirculationCore.describe(medium, @event), 500)!
            });
        }

        return row;
    }

    internal static CirculationCore.Tracked ToTracked(CirculationItemEntity row) =>
        new(
            row.ExternalId,
            row.Kind == CirculationCore.TrackedKind.LoanKind.Wire ? CirculationCore.TrackedKind.LoanKind : CirculationCore.TrackedKind.HoldKind,
            CirculationCore.TrackedStateModule.ofWire(row.State).OrNull() ?? CirculationCore.TrackedState.Pending,
            row.CatalogId.ToOption(),
            row.Title,
            Protocol.MediumModule.ofWire(row.Medium).OrNull() ?? Protocol.Medium.Physical,
            row.QueuePosition.ToValueOption(),
            row.QueueSize.ToValueOption(),
            row.PickupLocation.ToOption(),
            row.ReadyUntil.ToValueOption(),
            row.SuspendedUntil.ToValueOption(),
            row.DueAt.ToValueOption(),
            row.RemindedFor.ToValueOption());

    private async Task<SyncResult> FailSyncAsync(
        CirculationAccountEntity account,
        string problem,
        bool credentialsRefused,
        CancellationToken cancellationToken)
    {
        account.LastError = Clip(problem, 500);
        account.NeedsAttention |= credentialsRefused;
        await db.SaveChangesAsync(cancellationToken);
        return new SyncResult(false, problem, 0);
    }

    private static string Explain(Protocol.ProtocolError error) => error.Code switch
    {
        _ when error.Code.IsAuthRequired => "The library no longer accepts this card's PIN. Re-enter it on the Library cards page.",
        _ when error.Code.IsAlreadyHeld => "You already have a hold on this.",
        _ when error.Code.IsHoldLimit => "This card has reached the library's hold limit.",
        _ when error.Code.IsAccountBlocked => "The library has blocked this card — usually fines or an expired card.",
        _ when error.Code.IsNotHoldable => "The library does not take holds on this item.",
        _ => error.Message
    };

    private static bool IsTransport(Exception ex, CancellationToken cancellationToken) =>
        ex is PluginTransportException or HttpRequestException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>Only http(s) links are kept; a plugin's <c>javascript:</c> never reaches an href.</summary>
    private static string? HttpOnly(string? url) =>
        url is not null
        && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
        && url.Length <= 2000
            ? url
            : null;

    private static string? Clip(string? value, int limit) =>
        value is null || value.Length <= limit ? value : value[..limit];
}
