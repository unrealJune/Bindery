using Bindery.Host.Data;
using Bindery.Host.Downloads;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

public sealed class DownloadsModel(DownloadService downloads) : PageModel
{
    public IReadOnlyList<DownloadJobEntity> Jobs { get; private set; } = [];

    [FromQuery]
    public Guid? Highlight { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Jobs = await downloads.RecentAsync(100, cancellationToken);

    public async Task<PartialViewResult> OnGetRowsAsync(CancellationToken cancellationToken)
    {
        Jobs = await downloads.RecentAsync(100, cancellationToken);
        return Partial("_JobRows", Jobs);
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken cancellationToken)
    {
        TempData["Notice"] = await downloads.CancelAsync(id, cancellationToken)
            ? "Job cancelled."
            : "That job could not be cancelled.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid id, CancellationToken cancellationToken)
    {
        TempData["Notice"] = await downloads.RetryAsync(id, cancellationToken)
            ? "Job returned to the production floor."
            : "That job could not be retried.";

        return RedirectToPage();
    }
}
