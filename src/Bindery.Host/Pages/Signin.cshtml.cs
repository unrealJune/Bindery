using Bindery.Host.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Pages;

public sealed class SigninModel(IOptions<BinderyOptions> options) : PageModel
{
    public bool AuthDisabled => options.Value.Auth.Mode == AuthMode.None;

    public string? Problem { get; private set; }

    public IActionResult OnGet(string? returnUrl, string? error)
    {
        Problem = error;

        if (AuthDisabled || User.Identity?.IsAuthenticated == true)
        {
            return Redirect(SafeReturn(returnUrl));
        }

        return Page();
    }

    public IActionResult OnPost(string? returnUrl) =>
        Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) },
            OpenIdConnectDefaults.AuthenticationScheme);

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        if (AuthDisabled)
        {
            return RedirectToPage("/Index");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return SignOut(new AuthenticationProperties { RedirectUri = "/" }, OpenIdConnectDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Only local paths are honoured. An open redirect on the sign-in page is how a
    /// phishing link gets to wear your domain.
    /// </summary>
    private string SafeReturn(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}
