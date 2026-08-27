using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

/// <summary>
/// The shell a plugin's own HTML lives in.
/// </summary>
/// <remarks>
/// <para>
/// For a <c>fragment</c> plugin the markup is fetched by htmx from
/// <c>/plugins/{name}/ui/{path}</c> — the proxy route in <see cref="PluginUiEndpoints"/> —
/// and swapped into this page. This page exists so that navigating to a plugin screen, or
/// reloading one, produces a Bindery page rather than a bare fragment.
/// </para>
/// <para>
/// For a <c>sandboxed</c> plugin the same URL is instead the <c>src</c> of an iframe with no
/// <c>allow-same-origin</c>, so the plugin's document lands in an opaque origin and this
/// page never hosts its markup at all.
/// </para>
/// </remarks>
public sealed class PluginDeskModel(
    PluginRegistry registry,
    PluginUiProxy proxy,
    IAntiforgery antiforgery,
    IAuthenticationSchemeProvider schemes) : PageModel
{
    public PluginDescriptor Descriptor { get; private set; } = default!;

    public string FragmentUrl { get; private set; } = string.Empty;

    /// <summary>The plugin mount point. The bridge refuses any path outside it.</summary>
    public string BasePath { get; private set; } = string.Empty;

    /// <summary>The plugin-relative screen being shown, used to mark the current tab.</summary>
    public string CurrentPath { get; private set; } = "/";

    public IReadOnlyList<PluginNavEntry> Nav { get; private set; } = [];

    /// <summary>Whether the plugin's document is loaded into a sandboxed frame.</summary>
    public bool IsSandboxed { get; private set; }

    /// <summary>
    /// Handed to the bridge, never to the plugin. The frame cannot read this page's DOM, so
    /// putting it in an attribute here does not put it within the plugin's reach.
    /// </summary>
    public string CsrfToken { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(string name, string? path)
    {
        var descriptor = registry.Find(name);

        if (descriptor is null || !descriptor.IsUsable || descriptor.Manifest?.Ui.Mode.ServesHtml != true)
        {
            return NotFound();
        }

        Descriptor = descriptor;
        Nav = proxy.NavigationFor(descriptor);
        IsSandboxed = descriptor.Manifest.Ui.Mode.RunsInFrame;

        var relative = NormalizePath(path) is { } normalized && normalized != "/"
            ? normalized
            : descriptor.Manifest.Ui.Entry;

        FragmentUrl = proxy.BasePathFor(descriptor.Name) + relative;
        BasePath = proxy.BasePathFor(descriptor.Name);
        CurrentPath = relative;

        if (IsSandboxed)
        {
            CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

            // Issued here, from an ordinary authenticated page load, so that the frame's own
            // subresource requests carry something the Lax session cookie cannot follow them
            // into. See AuthSetup.FrameSchemeName for why this is a second cookie and not a
            // change to the first one. Absent in development, where the scheme is not
            // registered at all.
            if (User.Identity?.IsAuthenticated == true
                && await schemes.GetSchemeAsync(AuthSetup.FrameSchemeName) is not null)
            {
                await HttpContext.SignInAsync(AuthSetup.FrameSchemeName, User);
            }
        }

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
