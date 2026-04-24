using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LinkSafeServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Security;

namespace LinkSafeServer.Pages.Account;

[IgnoreAntiforgeryToken]
public sealed class LoginModel(ServerConfigService serverConfigService, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public string Message { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        if (!config.IsInitialized)
        {
            return RedirectToPage("/Setup");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        if (!ModelState.IsValid)
        {
            Message = "Username and password are required.";
            return Page();
        }

        var isValid =
            Input.Username.Equals(config.AdminUsername, StringComparison.OrdinalIgnoreCase) &&
            PasswordHasher.Verify(Input.Password, config.AdminPasswordHash);

        if (!isValid)
        {
            logger.LogWarning("Invalid web UI login attempt for {Username}.", Input.Username);
            Message = "Invalid username or password.";
            return Page();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, config.AdminUsername),
            new Claim(ClaimTypes.Role, "Admin")
        };

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return RedirectToPage("/Index");
    }

    public sealed class LoginInput
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }
}
