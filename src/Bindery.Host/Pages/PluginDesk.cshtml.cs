using Bindery.Host.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

/// <summary>
/// The shell a plugin's own HTML lives in.
/// </summary>
/// <remarks>
/// The fragment itself is fetched by htmx from <c>/plugins/{name}/ui/{path}</c> — the
/// proxy route in <see cref="PluginUiEndpoints"/>. This page exists so that navigating to
/// a plugin screen, or reloading one, produces a Bindery page rather than a bare fragment.
/// </remarks>
public sealed class PluginDeskModel(PluginRegistry registry, PluginUiProxy proxy) : PageModel
{
    public PluginDescriptor Descriptor { get; private set; } = default!;

    public string FragmentUrl { get; private set; } = string.Empty;

    public IReadOnlyList<PluginNavEntry> Nav { get; private set; } = [];

    public IActionResult OnGet(string name, string? path)
    {
        var descriptor = registry.Find(name);

        if (descriptor is null || !descriptor.IsUsable || descriptor.Manifest?.Ui.Mode.ServesHtml != true)
        {
            return NotFound();
        }

        Descriptor = descriptor;
        Nav = proxy.NavigationFor(descriptor);

        var relative = NormalizePath(path);
        FragmentUrl = proxy.BasePathFor(descriptor.Name) + relative;

        return Page();
    }

    /// <summary>
    /// Keeps the requested screen inside the plugin's own mount point. Anything that tries
    /// to climb out of it, or to another host, is discarded rather than corrected.
    /// </summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var candidate = path.StartsWith('/') ? path : "/" + path;

        if (candidate.StartsWith("//", StringComparison.Ordinal)
            || candidate.Contains("..", StringComparison.Ordinal)
            || candidate.Contains('\\', StringComparison.Ordinal))
        {
            return "/";
        }

        return candidate;
    }
}
