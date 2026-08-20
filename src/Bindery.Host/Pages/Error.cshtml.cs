using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bindery.Host.Pages;

public sealed class ErrorModel : PageModel
{
    /// <summary>
    /// The correlation id and nothing else. An error page that echoes exception text is a
    /// disclosure bug wearing a helpful face.
    /// </summary>
    public string? RequestId { get; private set; }

    public void OnGet() => RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
}
