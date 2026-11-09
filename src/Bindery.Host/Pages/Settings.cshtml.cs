using Bindery.Core;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

/// <summary>
/// Everything a person sets up once: their e-readers, their library cards, and each plugin.
/// </summary>
/// <remarks>
/// A hub of links rather than a page of forms. Each plugin's settings live on its own page,
/// beside its screens, so one plugin's fields are never in the way of another's.
/// </remarks>
public sealed class SettingsModel(PluginRegistry registry) : PageModel
{
    public IReadOnlyList<PluginDescriptor> Plugins { get; private set; } = [];

    public void OnGet() => Plugins = registry.All;
}
