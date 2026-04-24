using System.ComponentModel.DataAnnotations;
using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Security;

namespace LinkSafeServer.Pages;

public sealed class SetupModel(ServerConfigService serverConfigService) : PageModel
{
    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public string Message { get; set; } = string.Empty;
    public bool Success { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        if (config.IsInitialized)
        {
            return RedirectToPage("/Account/Login");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        if (config.IsInitialized)
        {
            return RedirectToPage("/Account/Login");
        }

        if (!ModelState.IsValid)
        {
            Message = "Please complete all setup fields.";
            return Page();
        }

        if (!string.Equals(Input.Password, Input.ConfirmPassword, StringComparison.Ordinal))
        {
            Message = "Passwords do not match.";
            return Page();
        }

        await serverConfigService.InitializeAdminAsync(Input.AdminUsername.Trim(), PasswordHasher.Hash(Input.Password), cancellationToken);
        Success = true;
        Message = "First setup completed. Please sign in with the new admin account.";
        return RedirectToPage("/Account/Login");
    }

    public sealed class SetupInput
    {
        [Required]
        public string AdminUsername { get; set; } = "admin";

        [Required]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
