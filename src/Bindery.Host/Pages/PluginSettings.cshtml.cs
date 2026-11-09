using Bindery.Core;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

/// <summary>
/// One plugin's settings tab. Tier 1 of the UI story: every plugin gets a settings form and
/// an action form rendered from its manifest, whether or not it ships any HTML of its own.
/// </summary>
/// <remarks>
/// Settings come in two sections, by the field's manifest scope (protocol §4.1). The
/// installation's are an admin's to see and change; everyone gets "your settings" for the
/// user-scoped fields, and sees only their own values there.
/// </remarks>
public sealed class PluginSettingsModel(
    PluginRegistry registry,
    PluginSettingsStore settings,
    PluginActionService actions,
    PluginUiProxy proxy) : PageModel
{
    /// <summary>The form value that selects the personal section; anything else is install.</summary>
    public const string PersonalScope = "user";

    public PluginCard Card { get; private set; } = default!;

    public IReadOnlyList<PluginNavEntry> Nav { get; private set; } = [];

    public bool IsAdmin => Identity.IsAdmin(User);

    public async Task<IActionResult> OnGetAsync(string name, CancellationToken cancellationToken)
    {
        if (registry.Find(name) is not { } descriptor)
        {
            return NotFound();
        }

        Nav = descriptor.IsUsable ? proxy.NavigationFor(descriptor, IsAdmin) : [];
        Card = await LoadAsync(descriptor, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRefreshAsync(string name, CancellationToken cancellationToken)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var descriptor = registry.Find(name);

        if (descriptor is null)
        {
            return NotFound();
        }

        var refreshed = await registry.RefreshAsync(descriptor.Entry, cancellationToken);

        TempData["Notice"] = refreshed.IsUsable
            ? $"{refreshed.DisplayName} answered: v{refreshed.Manifest!.Version}."
            : $"{refreshed.DisplayName} is not reachable: {refreshed.Error}";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveAsync(string name, string? scope, CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(name);

        if (descriptor?.Manifest is null)
        {
            return NotFound();
        }

        if (OwnerFor(scope) is not { } section)
        {
            return Forbid();
        }

        var manifest = descriptor.Manifest;
        var submitted = Collect(PluginSettingsStore.FieldsFor(manifest, section.Owner));
        var current = await settings.GetForDisplayAsync(manifest, section.Owner, cancellationToken);
        var problems = PluginSettingsStore.Validate(manifest, section.Owner, submitted, current.SecretsSet);

        if (problems.Count > 0)
        {
            TempData["Notice"] = string.Join(" ", problems);
            return RedirectToPage();
        }

        await settings.SaveAsync(manifest, section.Owner, submitted, cancellationToken);
        TempData["Notice"] = section.Owner is null
            ? $"Installation settings saved for {descriptor.DisplayName}."
            : $"Your settings saved for {descriptor.DisplayName}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearAsync(string name, string key, string? scope, CancellationToken cancellationToken)
    {
        if (registry.Find(name) is null)
        {
            return NotFound();
        }

        if (OwnerFor(scope) is not { } section)
        {
            return Forbid();
        }

        await settings.ClearAsync(name, key, section.Owner, cancellationToken);
        TempData["Notice"] = "Value cleared.";
        return RedirectToPage();
    }

    /// <summary>
    /// Runs a declared action and swaps only its result panel back, so a long search does
    /// not reload the whole page.
    /// </summary>
    public async Task<IActionResult> OnPostRunAsync(string name, string action, CancellationToken cancellationToken)
    {
        var declared = actions.FindAction(name, action);

        if (declared is null)
        {
            return NotFound();
        }

        var input = Collect(declared.Input.AsList())
            .ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.Ordinal);

        var result = await actions.InvokeAsync(name, action, input, Person.Of(User), cancellationToken);

        return Partial("_ActionResult", result);
    }

    /// <summary>
    /// Which section a post edits: the person's own, or — for an admin only — the
    /// installation's. Null means "not yours to change".
    /// </summary>
    private Section? OwnerFor(string? scope) =>
        scope == PersonalScope
            ? new Section(Identity.SubjectOf(User))
            : IsAdmin ? new Section(null) : null;

    private sealed record Section(string? Owner);

    private Dictionary<string, string?> Collect(IReadOnlyList<Protocol.ConfigField> fields)
    {
        var submitted = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            var name = "field:" + field.Key;

            if (!Request.Form.ContainsKey(name))
            {
                // A bool renders as a checkbox, and an unchecked checkbox posts nothing at
                // all. Absence there means "false", not "leave alone".
                if (field.Type.IsBoolField)
                {
                    submitted[field.Key] = "false";
                }

                continue;
            }

            submitted[field.Key] = field.Type.IsBoolField
                ? "true"
                : Request.Form[name].ToString();
        }

        return submitted;
    }

    private async Task<PluginCard> LoadAsync(PluginDescriptor descriptor, CancellationToken cancellationToken)
    {
        var person = Person.Of(User);

        if (descriptor.Manifest is not { } manifest)
        {
            return new PluginCard(descriptor, null, new PluginSettingsView(new Dictionary<string, string>(), new HashSet<string>()), []);
        }

        var install = person.IsAdmin
            ? await settings.GetForDisplayAsync(manifest, null, cancellationToken)
            : null;
        var mine = await settings.GetForDisplayAsync(manifest, person.Subject, cancellationToken);

        return new PluginCard(descriptor, install, mine, PluginActionService.AvailableTo(manifest, person));
    }
}

/// <param name="CurrentPath">The plugin screen showing; null when it is the settings tab.</param>
public sealed record PluginTabsView(
    PluginDescriptor Descriptor,
    IReadOnlyList<PluginNavEntry> Nav,
    string? CurrentPath);

/// <param name="Install">The installation's settings; null unless the viewer is an admin.</param>
/// <param name="Mine">The viewer's own values for the user-scoped fields.</param>
/// <param name="Actions">The declared actions the viewer may run.</param>
public sealed record PluginCard(
    PluginDescriptor Descriptor,
    PluginSettingsView? Install,
    PluginSettingsView Mine,
    IReadOnlyList<Protocol.PluginAction> Actions);

/// <summary>One settings section of a plugin card; <paramref name="Scope"/> is null for install.</summary>
public sealed record SettingsSectionView(
    string Plugin,
    string? Scope,
    IReadOnlyList<Protocol.ConfigField> Fields,
    PluginSettingsView Values);

/// <summary>One declared field, plus what the UI is allowed to know about its value.</summary>
public sealed record ConfigFieldView(Protocol.ConfigField Field, string Value, bool SecretIsSet);
