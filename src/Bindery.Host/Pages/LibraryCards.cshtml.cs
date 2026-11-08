using Bindery.Core;
using Bindery.Host.Circulation;
using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Bindery.Host.Notifications;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Pages;

/// <summary>
/// A person's own library cards, and how they want to be told about them.
/// </summary>
/// <remarks>
/// Everything on this page belongs to the signed-in person and no one else. Account forms
/// are rendered from the plugin's own <c>accountFields</c> schema with the same partial the
/// plugin settings page uses, so a PIN is a write-only password box here too.
/// </remarks>
public sealed class LibraryCardsModel(
    CirculationAccountStore accounts,
    CirculationService circulation,
    NotificationService notifications,
    IEnumerable<INotificationChannel> channels,
    IOptions<BinderyOptions> options) : PageModel
{
    public IReadOnlyList<PluginDescriptor> Plugins { get; private set; } = [];

    public IReadOnlyList<CirculationAccountEntity> Accounts { get; private set; } = [];

    public NotificationPreferences Preferences { get; private set; } = new(null, true, new HashSet<string>());

    public IReadOnlyList<NotificationEntity> Recent { get; private set; } = [];

    public bool EmailConfigured => channels.Any(channel => channel.IsConfigured);

    public bool EmailPickupMode => !string.IsNullOrWhiteSpace(options.Value.Notifications.Email.PickupDirectory);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var owner = Identity.SubjectOf(User);

        Plugins = accounts.CirculationPlugins;
        Accounts = await accounts.ListAsync(owner, cancellationToken);
        Preferences = await notifications.EnsureAsync(owner, Identity.EmailOf(User), cancellationToken);
        Recent = await notifications.RecentAsync(owner, 12, cancellationToken);
    }

    public async Task<IActionResult> OnPostConnectAsync(string plugin, CancellationToken cancellationToken)
    {
        var owner = Identity.SubjectOf(User);
        var spec = accounts.Resolve(plugin).Spec;

        if (spec is null)
        {
            return NotFound();
        }

        var result = await accounts.ConnectAsync(owner, plugin, Collect(spec), cancellationToken);

        if (result.Account is null)
        {
            TempData["Notice"] = result.Problem;
            return RedirectToPage();
        }

        // Ensure a delivery address exists before the first sync can produce anything to send.
        await notifications.EnsureAsync(owner, Identity.EmailOf(User), cancellationToken);
        var sync = await circulation.SyncAsync(result.Account, cancellationToken);

        TempData["Notice"] = sync.Succeeded
            ? $"Connected {result.Account.LibraryName}. Your holds and loans are on the board."
            : $"Connected {result.Account.LibraryName}, but the first sync failed: {sync.Problem}";

        return RedirectToPage("/Holds");
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid account, CancellationToken cancellationToken)
    {
        var owner = Identity.SubjectOf(User);
        var existing = await accounts.FindAsync(owner, account, cancellationToken);
        var spec = existing is null ? null : accounts.Resolve(existing.Plugin).Spec;

        if (existing is null || spec is null)
        {
            return NotFound();
        }

        var result = await accounts.UpdateAsync(owner, account, Collect(spec), cancellationToken);
        TempData["Notice"] = result.Account is null ? result.Problem : $"Updated {result.Account.LibraryName}.";

        if (result.Account is not null)
        {
            await circulation.SyncAsync(result.Account, cancellationToken);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid account, CancellationToken cancellationToken)
    {
        var removed = await accounts.RemoveAsync(Identity.SubjectOf(User), account, cancellationToken);

        TempData["Notice"] = removed
            ? "Card removed. Nothing changed at the library — your holds there are untouched."
            : "That card was not connected.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPreferencesAsync(
        string? email,
        bool emailEnabled,
        string[] categories,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(email) && !EmailChannel.IsValidAddress(email))
        {
            TempData["Notice"] = "That email address does not look right.";
            return RedirectToPage();
        }

        var known = categories.Where(key => NotificationCategories.Find(key) is not null);
        await notifications.SaveAsync(Identity.SubjectOf(User), email, emailEnabled, known, cancellationToken);

        TempData["Notice"] = "Notification settings saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        var owner = Identity.SubjectOf(User);
        await notifications.EnsureAsync(owner, Identity.EmailOf(User), cancellationToken);

        await notifications.EnqueueAsync(
            owner,
            NotificationCategories.Test,
            "bindery",
            "Bindery test message",
            "If you can read this, Bindery can reach you.\n\nHolds becoming ready, pickup deadlines, and due dates will arrive the same way.\n\n— Bindery",
            cancellationToken);

        TempData["Notice"] = EmailConfigured
            ? "Test message queued; it should arrive within a minute."
            : "Test message recorded, but this server has no email delivery configured, so it will not be sent.";

        return RedirectToPage();
    }

    public IReadOnlyDictionary<string, string> PlainFields(CirculationAccountEntity account) =>
        CirculationAccountStore.PlainFields(account);

    public Protocol.CirculationSpec? SpecFor(string plugin) => accounts.Resolve(plugin).Spec;

    /// <summary>Collects a form rendered by _ConfigField, whose inputs are named "field:{key}".</summary>
    private Dictionary<string, string?> Collect(Protocol.CirculationSpec spec)
    {
        var submitted = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var field in spec.AccountFields.AsList())
        {
            var name = "field:" + field.Key;

            if (field.Type.IsBoolField)
            {
                submitted[field.Key] = Request.Form.ContainsKey(name) ? "true" : "false";
            }
            else if (Request.Form.ContainsKey(name))
            {
                submitted[field.Key] = Request.Form[name].ToString();
            }
        }

        return submitted;
    }
}
