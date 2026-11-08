using System.Net;
using System.Net.Http.Json;
using Bindery.Core;
using Bindery.Host.Circulation;
using Bindery.Host.Data;
using Bindery.Host.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bindery.Host.Tests;

/// <summary>
/// Library circulation end to end: a person's card, the stub library over real HTTP, the
/// core's lifecycle rules, the database, and the mail that comes out the other side.
/// </summary>
[Collection(BinderyCollection.Name)]
public sealed class CirculationTests(BinderyFixture fixture)
{
    private const string Owner = "dev";

    private static int _cards;

    /// <summary>A fresh card number per test, so tests never see each other's holds.</summary>
    private static string NewCard() => $"2900001{Interlocked.Increment(ref _cards):0000000}";

    private async Task<CirculationAccountEntity> ConnectAsync(IServiceScope scope, string card, string owner = Owner)
    {
        var store = scope.ServiceProvider.GetRequiredService<CirculationAccountStore>();
        var result = await store.ConnectAsync(
            owner,
            fixture.Plugin.Name,
            new Dictionary<string, string?> { ["card"] = card, ["pin"] = StubPlugin.GoodPin },
            CancellationToken.None);

        Assert.Null(result.Problem);
        return result.Account!;
    }

    private void SetHolds(string card, params object[] holds)
    {
        fixture.Plugin.CardHolds[card] = [.. holds];
    }

    private void SetLoans(string card, params object[] loans)
    {
        fixture.Plugin.CardLoans[card] = [.. loans];
    }

    private static async Task<CirculationItemEntity> ItemAsync(IServiceScope scope, Guid account, string externalId)
    {
        var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
        return await db.CirculationItems.AsNoTracking()
            .Where(item => item.AccountId == account && item.ExternalId == externalId)
            .OrderByDescending(item => item.FirstSeenAt)
            .FirstAsync();
    }

    private static async Task<List<NotificationEntity>> NotificationsAsync(IServiceScope scope, string titleFragment) =>
        await scope.ServiceProvider.GetRequiredService<BinderyDbContext>().Notifications.AsNoTracking()
            .Where(n => n.Recipient == Owner && n.Subject.Contains(titleFragment))
            .OrderBy(n => n.CreatedAt)
            .ToListAsync();

    // ------------------------------------------------------------ accounts

    [Fact]
    public async Task A_wrong_pin_is_refused_and_nothing_is_stored()
    {
        using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<CirculationAccountStore>();
        var card = NewCard();

        var result = await store.ConnectAsync(
            Owner, fixture.Plugin.Name, new Dictionary<string, string?> { ["card"] = card, ["pin"] = "0000" },
            CancellationToken.None);

        Assert.Null(result.Account);
        Assert.Contains("did not accept", result.Problem);
        Assert.DoesNotContain(await store.ListAsync(Owner, CancellationToken.None), a => a.FieldsJson.Contains(card));
    }

    [Fact]
    public async Task The_pin_is_encrypted_at_rest_and_the_card_belongs_to_its_owner_alone()
    {
        using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<CirculationAccountStore>();
        var card = NewCard();
        var account = await ConnectAsync(scope, card);

        Assert.Equal("Stubville Public Library", account.LibraryName);
        Assert.Equal($"card ending {card[^4..]}", account.DisplayName);
        Assert.DoesNotContain(StubPlugin.GoodPin, account.ProtectedSecrets);
        Assert.DoesNotContain(StubPlugin.GoodPin, account.FieldsJson);
        Assert.Equal(StubPlugin.GoodPin, store.ToProtocol(account).Fields["pin"]);

        // Somebody else, guessing the id, sees nothing and can remove nothing.
        Assert.Null(await store.FindAsync("someone-else", account.Id, CancellationToken.None));
        Assert.False(await store.RemoveAsync("someone-else", account.Id, CancellationToken.None));
        Assert.Empty(await store.ListAsync("someone-else", CancellationToken.None));
        Assert.NotNull(await store.FindAsync(Owner, account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Search_honours_the_medium_filter()
    {
        using var scope = fixture.CreateScope();
        var account = await ConnectAsync(scope, NewCard());
        var circulation = scope.ServiceProvider.GetRequiredService<CirculationService>();

        var digital = await circulation.SearchAsync(Owner, "stub", Protocol.Medium.Digital, account.Id, 1, CancellationToken.None);
        var physical = await circulation.SearchAsync(Owner, "stub", Protocol.Medium.Physical, account.Id, 1, CancellationToken.None);

        var ebook = Assert.Single(Assert.Single(digital).Page!.Items);
        Assert.True(ebook.Medium.IsDigital);
        Assert.Equal("StubDrive", ebook.Provider.Value);
        Assert.True(Assert.Single(Assert.Single(physical).Page!.Items).Medium.IsPhysical);
    }

    // ------------------------------------------------------------ the lifecycle

    [Fact]
    public async Task A_hold_is_tracked_from_queue_to_shelf_to_loan_and_the_right_moments_are_mailed()
    {
        using var scope = fixture.CreateScope();
        var card = NewCard();
        var account = await ConnectAsync(scope, card);
        var circulation = scope.ServiceProvider.GetRequiredService<CirculationService>();
        var title = $"Lifecycle {card}";

        // A hold placed before the card was connected: recorded, not announced.
        SetHolds(card, new { id = "h1", itemId = "bib-9", title, medium = "physical", state = "pending", queuePosition = 3, queueSize = 7 });
        Assert.True((await circulation.SyncAsync(account, CancellationToken.None)).Succeeded);
        Assert.Equal("pending", (await ItemAsync(scope, account.Id, "h1")).State);
        Assert.Empty(await NotificationsAsync(scope, title));

        SetHolds(card, new { id = "h1", itemId = "bib-9", title, medium = "physical", state = "in_transit" });
        await circulation.SyncAsync(account, CancellationToken.None);

        var until = DateTimeOffset.UtcNow.AddDays(6).ToString("yyyy-MM-dd");
        SetHolds(card, new
        {
            id = "h1", itemId = "bib-9", title, medium = "physical", state = "ready", readyUntil = until,
            pickupLocation = new { id = "2", name = "Harbour Branch" }
        });
        await circulation.SyncAsync(account, CancellationToken.None);

        var ready = await ItemAsync(scope, account.Id, "h1");
        Assert.Equal("ready", ready.State);
        Assert.Equal("Harbour Branch", ready.PickupLocation);

        // Picked up: the hold disappears and a loan for the same catalog item appears.
        SetHolds(card);
        SetLoans(card, new { id = "c1", itemId = "bib-9", title, medium = "physical", dueAt = DateTimeOffset.UtcNow.AddDays(21).ToString("O") });
        await circulation.SyncAsync(account, CancellationToken.None);

        Assert.Equal("collected", (await ItemAsync(scope, account.Id, "h1")).State);
        Assert.Equal("checked_out", (await ItemAsync(scope, account.Id, "c1")).State);

        var mailed = await NotificationsAsync(scope, title);
        Assert.Equal(["hold-in-transit", "hold-ready"], mailed.Select(n => n.Category));
        Assert.StartsWith("Ready for pickup:", mailed[1].Subject);
        Assert.Contains("Harbour Branch", mailed[1].Body);

        var history = await circulation.EventsAsync(Owner, ready.Id, CancellationToken.None);
        Assert.Equal(["collected", "ready", "in-transit", "hold-placed"], history.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_ready_hold_left_on_the_shelf_expires_and_that_is_mailed()
    {
        using var scope = fixture.CreateScope();
        var card = NewCard();
        var account = await ConnectAsync(scope, card);
        var circulation = scope.ServiceProvider.GetRequiredService<CirculationService>();
        var title = $"Forgotten {card}";

        SetHolds(card, new { id = "h1", itemId = "bib-3", title, medium = "physical", state = "ready" });
        await circulation.SyncAsync(account, CancellationToken.None);
        SetHolds(card);
        await circulation.SyncAsync(account, CancellationToken.None);

        Assert.Equal("expired", (await ItemAsync(scope, account.Id, "h1")).State);
        Assert.Equal(["hold-ended"], (await NotificationsAsync(scope, title)).Select(n => n.Category));
    }

    [Fact]
    public async Task A_failed_snapshot_concludes_nothing()
    {
        using var scope = fixture.CreateScope();
        var card = NewCard();
        var account = await ConnectAsync(scope, card);
        var circulation = scope.ServiceProvider.GetRequiredService<CirculationService>();

        SetHolds(card, new { id = "h1", itemId = "bib-4", title = "Still here", medium = "physical", state = "pending" });
        await circulation.SyncAsync(account, CancellationToken.None);

        fixture.Plugin.FailSnapshots = true;

        try
        {
            SetHolds(card);
            var result = await circulation.SyncAsync(account, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.NotNull(account.LastError);
            Assert.False(account.NeedsAttention);
            // Had the failure been read as "no holds", this would now say "closed".
            Assert.Equal("pending", (await ItemAsync(scope, account.Id, "h1")).State);
        }
        finally
        {
            fixture.Plugin.FailSnapshots = false;
        }
    }

    [Fact]
    public async Task A_refused_pin_pauses_syncing_until_it_is_re_entered()
    {
        using var scope = fixture.CreateScope();
        var card = NewCard();
        var account = await ConnectAsync(scope, card);
        var db = scope.ServiceProvider.GetRequiredService<BinderyDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<CirculationAccountStore>();

        // The library changes its mind about the PIN (the person changed it there).
        var protector = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
            .CreateProtector("Bindery.CirculationAccounts.v1");
        account.ProtectedSecrets = Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Protect(protector, """{"pin":"9999"}""");
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<CirculationService>().SyncAsync(account, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(account.NeedsAttention);

        var updated = await store.UpdateAsync(
            Owner, account.Id, new Dictionary<string, string?> { ["card"] = card, ["pin"] = StubPlugin.GoodPin }, CancellationToken.None);

        Assert.Null(updated.Problem);
        Assert.False(updated.Account!.NeedsAttention);
    }

    [Fact]
    public async Task Placing_and_cancelling_a_hold_goes_through_the_plugin_and_mails_nobody()
    {
        using var scope = fixture.CreateScope();
        var card = NewCard();
        var account = await ConnectAsync(scope, card);
        var circulation = scope.ServiceProvider.GetRequiredService<CirculationService>();

        var refused = await circulation.PlaceHoldAsync(Owner, account.Id, "bib-2", null, CancellationToken.None);
        Assert.False(refused.Succeeded);
        Assert.Contains("does not take holds", refused.Message);

        var badBranch = await circulation.PlaceHoldAsync(Owner, account.Id, "bib-1", "99", CancellationToken.None);
        Assert.False(badBranch.Succeeded);

        var placed = await circulation.PlaceHoldAsync(Owner, account.Id, "bib-1", "2", CancellationToken.None);
        Assert.True(placed.Succeeded, placed.Message);

        var hold = await ItemAsync(scope, account.Id, "hold-1");
        Assert.Equal("pending", hold.State);

        // Someone else cannot cancel it, even knowing the id.
        var stranger = await circulation.OperateAsync("someone-else", hold.Id, Protocol.HoldOperation.CancelHold, null, CancellationToken.None);
        Assert.False(stranger.Succeeded);

        var cancelled = await circulation.OperateAsync(Owner, hold.Id, Protocol.HoldOperation.CancelHold, null, CancellationToken.None);
        Assert.True(cancelled.Succeeded);
        Assert.Contains("hold-1:cancel", fixture.Plugin.HoldOperations);
        Assert.Equal("cancelled", (await ItemAsync(scope, account.Id, "hold-1")).State);

        // The next snapshot's absence is not mistaken for the library closing it.
        await circulation.SyncAsync(account, CancellationToken.None);
        Assert.Equal("cancelled", (await ItemAsync(scope, account.Id, "hold-1")).State);
        Assert.Empty(await NotificationsAsync(scope, "A Shelf Copy"));
    }

    // ------------------------------------------------------------ delivery

    [Fact]
    public async Task A_ready_hold_arrives_as_an_email_and_a_muted_category_does_not()
    {
        using var scope = fixture.CreateScope();
        var card = NewCard();
        var account = await ConnectAsync(scope, card);
        var circulation = scope.ServiceProvider.GetRequiredService<CirculationService>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var title = $"Delivered {card}";

        await notifications.SaveAsync(
            Owner, "reader@stub.invalid", true,
            NotificationCategories.All.Select(c => c.Key).Where(key => key != "hold-in-transit"),
            CancellationToken.None);

        SetHolds(card, new { id = "h1", itemId = "bib-5", title, medium = "digital", state = "pending" });
        await circulation.SyncAsync(account, CancellationToken.None);
        SetHolds(card, new { id = "h1", itemId = "bib-5", title, medium = "digital", state = "in_transit" });
        await circulation.SyncAsync(account, CancellationToken.None);
        SetHolds(card, new { id = "h1", itemId = "bib-5", title, medium = "digital", state = "ready" });
        await circulation.SyncAsync(account, CancellationToken.None);

        await fixture.Services.GetRequiredService<NotificationWorker>().DrainAsync(CancellationToken.None);

        var rows = await NotificationsAsync(scope, title);
        Assert.Equal(NotificationStatus.Suppressed, rows.Single(n => n.Category == "hold-in-transit").Status);
        Assert.Equal(NotificationStatus.Sent, rows.Single(n => n.Category == "hold-ready").Status);

        var mail = Directory.GetFiles(fixture.MailPath, "*.eml")
            .Select(File.ReadAllText)
            .Single(text => text.Contains(card));

        Assert.Contains("To: reader@stub.invalid", mail);
        // A digital hold reads as "borrow", never "pick up".
        Assert.Contains("Ready to borrow", mail);
        Assert.DoesNotContain(StubPlugin.GoodPin, mail);
    }

    [Fact]
    public async Task A_line_break_in_a_library_title_cannot_reach_a_mail_header()
    {
        using var scope = fixture.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

        var row = await notifications.EnqueueAsync(
            Owner, "hold-ready", "test", "Ready: Evil\r\nBcc: victim@stub.invalid", "body", CancellationToken.None);

        Assert.DoesNotContain('\n', row.Subject);
        Assert.DoesNotContain('\r', row.Subject);
    }

    // ------------------------------------------------------------ pages

    [Theory]
    [InlineData("/borrow")]
    [InlineData("/borrow?q=stub&medium=digital")]
    [InlineData("/holds")]
    [InlineData("/library-cards")]
    public async Task The_circulation_pages_render(string path)
    {
        using var scope = fixture.CreateScope();
        await ConnectAsync(scope, NewCard());

        using var response = await fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(StubPlugin.GoodPin, html);

        if (path.Contains("medium=digital"))
        {
            Assert.Contains("A Licence", html);
            Assert.DoesNotContain("A Shelf Copy", html);
            Assert.Contains("Borrow in StubDrive", html);
        }
    }

    [Fact]
    public async Task A_download_that_fails_for_good_tells_whoever_queued_it()
    {
        fixture.Plugin.FailDownloads = true;

        try
        {
            using var response = await fixture.Client.PostAsJsonAsync(
                "/api/downloads", new { url = "https://stub.invalid/works/9031" });

            var created = await response.Content.ReadFromJsonAsync<JsonElementJob>();
            var job = await fixture.WaitForJobAsync(created!.Id, TimeSpan.FromSeconds(60));
            Assert.Equal("failed", job.Status);

            using var scope = fixture.CreateScope();
            var rows = await NotificationsAsync(scope, "works/9031");
            Assert.Equal(NotificationCategories.DownloadFailed, Assert.Single(rows).Category);
        }
        finally
        {
            fixture.Plugin.FailDownloads = false;
        }
    }
}
