using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Constants;
using Shared.Models;

namespace LinkSafeServer.Pages;

public sealed class SettingsModel(
    ServerConfigService serverConfigService,
    StorageSecurityService storageSecurityService) : PageModel
{
    [BindProperty]
    public ServerConfig Config { get; set; } = new();

    [BindProperty]
    public string Action { get; set; } = "save";

    [BindProperty]
    public List<string> SelectedFolders { get; set; } = [];

    public string[] FolderOptions => LinkSafeDefaults.KnownFolders;
    public string Message { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string ServerLogPath => LinkSafeDefaults.ServerLogFilePath;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var existingConfig = await serverConfigService.GetAsync(cancellationToken);
        existingConfig.BackupRoot = Config.BackupRoot;
        existingConfig.BackupFolders = SelectedFolders
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        existingConfig.DelayBeforeCopySeconds = Config.DelayBeforeCopySeconds;
        existingConfig.StableFileCheckSeconds = Config.StableFileCheckSeconds;

        await serverConfigService.SaveAsync(existingConfig, cancellationToken);

        if (Action.Equals("permissions", StringComparison.OrdinalIgnoreCase))
        {
            var result = await storageSecurityService.ApplySecurePermissionsAsync(existingConfig, cancellationToken);
            Success = result.Success;
            Message = result.Message;
        }
        else
        {
            Success = true;
            Message = "Settings saved successfully.";
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Config = await serverConfigService.GetAsync(cancellationToken);
        SelectedFolders = [.. Config.BackupFolders];
    }
}
