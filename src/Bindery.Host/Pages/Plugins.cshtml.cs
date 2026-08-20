using Bindery.Core;
using Bindery.Host.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

/// <summary>
/// Tier 1 of the UI story: every plugin gets a settings form and an action form rendered
/// from its manifest, whether or not it ships any HTML of its own.
/// </summary>
public sealed class PluginsModel(
    PluginRegistry registry,
    PluginSettingsStore settings,
    PluginActionService actions) : PageModel
{
    public IReadOnlyList<PluginCard> Cards { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostRefreshAsync(string plugin, CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(plugin);

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

    public async Task<IActionResult> OnPostSaveAsync(string plugin, CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(plugin);

        if (descriptor?.Manifest is null)
        {
            return NotFound();
        }

        var manifest = descriptor.Manifest;
        var submitted = Collect(manifest.Config.AsList());
        var current = await settings.GetForDisplayAsync(manifest, cancellationToken);
        var problems = PluginSettingsStore.Validate(manifest, submitted, current.SecretsSet);

        if (problems.Count > 0)
        {
            TempData["Notice"] = string.Join(" ", problems);
            return RedirectToPage();
        }

        await settings.SaveAsync(manifest, submitted, cancellationToken);
        TempData["Notice"] = $"Settings saved for {descriptor.DisplayName}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearAsync(string plugin, string key, CancellationToken cancellationToken)
    {
        if (registry.Find(plugin) is null)
        {
            return NotFound();
        }

        await settings.ClearAsync(plugin, key, cancellationToken);
        TempData["Notice"] = "Value cleared.";
        return RedirectToPage();
    }

    /// <summary>
    /// Runs a declared action and swaps only its result panel back, so a long search does
    /// not reload the whole page.
    /// </summary>
    public async Task<IActionResult> OnPostRunAsync(string plugin, string action, CancellationToken cancellationToken)
    {
        var declared = actions.FindAction(plugin, action);

        if (declared is null)
        {
            return NotFound();
        }

        var input = Collect(declared.Input.AsList())
            .ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.Ordinal);

        var result = await actions.InvokeAsync(plugin, action, input, cancellationToken);

        return Partial("_ActionResult", result);
    }

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

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var cards = new List<PluginCard>();

        foreach (var descriptor in registry.All)
        {
            var view = descriptor.Manifest is null
                ? new PluginSettingsView(new Dictionary<string, string>(), new HashSet<string>())
                : await settings.GetForDisplayAsync(descriptor.Manifest, cancellationToken);

            cards.Add(new PluginCard(descriptor, view));
        }

        Cards = cards;
    }
}

public sealed record PluginCard(PluginDescriptor Descriptor, PluginSettingsView Settings);

/// <summary>One declared field, plus what the UI is allowed to know about its value.</summary>
public sealed record ConfigFieldView(Protocol.ConfigField Field, string Value, bool SecretIsSet);
