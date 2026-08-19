using System.ComponentModel.DataAnnotations;
using Bindery.Core;
using Bindery.Host.Catalog;
using Bindery.Host.Downloads;
using Bindery.Host.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

public sealed class IndexModel(
    CatalogService catalog,
    DownloadService downloads,
    PluginRegistry plugins) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter the source URL to file.")]
    public string SourceUrl { get; set; } = string.Empty;

    [BindProperty]
    public string? PreferredPlugin { get; set; }

    public IReadOnlyList<Domain.Book> RecentBooks { get; private set; } = [];

    public IReadOnlyList<Data.DownloadJobEntity> ActiveJobs { get; private set; } = [];

    public IReadOnlyList<PluginChoice> Plugins { get; private set; } = [];

    public int TotalBooks { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostQueueAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var result = await downloads.EnqueueAsync(
            SourceUrl,
            User.Identity?.Name,
            PreferredPlugin,
            cancellationToken);

        if (!result.Accepted)
        {
            ModelState.AddModelError(string.Empty, result.Problem ?? "The source could not be queued.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        TempData["Notice"] = "Source accepted. The job is now on the production floor.";
        return RedirectToPage("/Downloads", new { highlight = result.Job!.Id });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var recent = await catalog.RecentAsync(0, 8, cancellationToken);
        RecentBooks = recent.Items;
        TotalBooks = recent.Total;
        ActiveJobs = await downloads.ActiveAsync(cancellationToken);
        Plugins =
        [
            .. plugins.Usable.Select(plugin => new PluginChoice(plugin.Name, plugin.DisplayName))
        ];
    }
}

public sealed record PluginChoice(string Name, string DisplayName);
