using Bindery.Host.Plugins;

namespace Bindery.Host.Ui;

/// <summary>
/// Naming the downloader a book came from, for the register's source badge.
/// </summary>
/// <remarks>
/// Goes through the registry rather than the stored string so a plugin's own display name
/// wins over its wire name, and falls back to the wire name for a plugin that has since
/// been uninstalled — a book keeps its provenance even when the thing that fetched it is
/// gone. No plugin is named here; the host stays ignorant of which ones exist.
/// </remarks>
public sealed class SourceLabels(PluginRegistry plugins)
{
    /// <summary>Shown for a book nothing downloaded: uploaded by hand, or found by a rescan.</summary>
    public const string Manual = "Manual";

    public bool IsManual(string? sourcePlugin) => string.IsNullOrWhiteSpace(sourcePlugin);

    public string For(string? sourcePlugin) =>
        IsManual(sourcePlugin) ? Manual : plugins.Find(sourcePlugin!)?.DisplayName ?? sourcePlugin!;

    public string For(Microsoft.FSharp.Core.FSharpOption<string>? sourcePlugin) => For(sourcePlugin.OrNull());
}
