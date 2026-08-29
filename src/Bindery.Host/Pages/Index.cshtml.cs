using System.ComponentModel.DataAnnotations;
using Bindery.Core;
using Bindery.Host.Catalog;
using Bindery.Host.Downloads;
using Bindery.Host.Library;
using Bindery.Host.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

public sealed class IndexModel(
    CatalogService catalog,
    DownloadService downloads,
    LibraryWriter library,
    PluginRegistry plugins) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter the source URL to file.")]
    public string SourceUrl { get; set; } = string.Empty;

    [BindProperty]
    public string? PreferredPlugin { get; set; }

    [BindProperty]
    [Display(Name = "Book file")]
    public IFormFile? Upload { get; set; }

    [BindProperty]
    [Display(Name = "Title")]
    [StringLength(500)]
    public string? UploadTitle { get; set; }

    [BindProperty]
    [Display(Name = "Author")]
    [StringLength(300)]
    public string? UploadAuthor { get; set; }

    public IReadOnlyList<Domain.Book> RecentBooks { get; private set; } = [];

    public IReadOnlyList<Data.DownloadJobEntity> ActiveJobs { get; private set; } = [];

    public IReadOnlyList<PluginChoice> Plugins { get; private set; } = [];

    public int TotalBooks { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    /// <summary>
    /// Files an uploaded book directly, without a job.
    /// </summary>
    /// <remarks>
    /// The queue exists to supervise something slow and fallible happening on another
    /// machine. An upload is neither: the bytes are already here, so it either lands on the
    /// volume before the response or it does not, and a job row for it would only be a
    /// ledger entry that was born complete.
    /// </remarks>
    public async Task<IActionResult> OnPostUploadAsync(CancellationToken cancellationToken)
    {
        // Only this form's fields are being submitted, so the URL field's Required rule is
        // not this handler's business.
        ModelState.Remove(nameof(SourceUrl));

        if (Upload is null || Upload.Length == 0)
        {
            ModelState.AddModelError(nameof(Upload), "Choose a book file to deposit.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        await using var content = Upload!.OpenReadStream();

        var result = await library.ImportAsync(
            content, Upload.FileName, UploadTitle, UploadAuthor, cancellationToken);

        if (!result.Accepted)
        {
            ModelState.AddModelError(nameof(Upload), result.Problem ?? "That file could not be filed.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        TempData["Notice"] = result.AlreadyHeld
            ? $"Already on the volume, byte for byte: “{result.Title}”."
            : $"Filed “{result.Title}” onto the volume.";

        return RedirectToPage("/Book", new { id = result.BookId });
    }

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
