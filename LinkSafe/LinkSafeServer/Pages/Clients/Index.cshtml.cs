using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Models;
using LinkSafeServer.Models;
using Shared.Constants;

namespace LinkSafeServer.Pages.Clients;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IndexModel(ClientRegistryService clientRegistryService, RemoteBackupService remoteBackups, ServerConfigService configs) : PageModel
{
    public List<RegisteredClientRecord> Clients { get; private set; } = [];
    public List<RemoteBackupRequest> Requests { get; private set; } = [];
    [TempData] public string? Notice { get; set; }
    [BindProperty] public List<string> SelectedClients { get; set; } = [];
    [BindProperty] public string? BulkAction { get; set; }
    [BindProperty] public List<string> SelectedFolders { get; set; } = [];
    [BindProperty] public string? CustomFoldersText { get; set; }
    public List<RegisteredClientRecord> ReviewedClients { get; private set; } = [];
    public string[] FolderOptions => LinkSafeDefaults.KnownFolders;
    public bool ShowReview { get; private set; }

    private async Task<bool> LoadSelectionAsync(CancellationToken token)
    {
        await OnGetAsync(token);
        try
        {
            if (!BulkClientSelection.Actions.Contains(BulkAction)) throw new ArgumentException("Choose a bulk action.");
            ReviewedClients = BulkClientSelection.Resolve(Clients, SelectedClients);
            ShowReview = true;
        }
        catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        return ModelState.IsValid;
    }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        if (await LoadSelectionAsync(cancellationToken) && BulkAction == "Settings")
        {
            var config = await configs.GetAsync(cancellationToken);
            SelectedFolders = [.. config.BackupFolders];
            CustomFoldersText = string.Join(Environment.NewLine, config.CustomFolders);
            ModelState.Remove(nameof(CustomFoldersText));
        }
        return Page();
    }

    public async Task<IActionResult> OnPostApplyAsync(bool confirmed, CancellationToken cancellationToken)
    {
        if (!await LoadSelectionAsync(cancellationToken)) return Page();
        if (!confirmed) { ModelState.AddModelError(string.Empty, "Review the selection and confirm the action."); return Page(); }
        try
        {
            if (BulkAction == "Backup")
            {
                var queued = 0;
                var skipped = new List<string>();
                foreach (var client in ReviewedClients)
                {
                    try { await remoteBackups.EnqueueAsync(client.DomainUser, client.ComputerName, cancellationToken); queued++; }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    { skipped.Add($"{client.ComputerName}: {ex.Message}"); }
                }
                Notice = $"{queued} backup requests queued or already pending; {skipped.Count} skipped. Queued does not mean completed. " + string.Join(" ", skipped.Take(3));
            }
            else
            {
                var paths = (CustomFoldersText ?? "").Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
                var count = await clientRegistryService.ApplyBulkAsync(SelectedClients, BulkAction!, SelectedFolders, paths, cancellationToken);
                Notice = BulkAction switch
                {
                    "Remove" => $"Removed {count} computers from the registry. All backup files were kept. Running agents may register again.",
                    "Settings" or "Defaults" => $"Updated backup settings for {count} computers. Agents apply them on their next policy refresh; local PC selections still take precedence. No backup files were deleted.",
                    _ => $"{BulkAction} applied to {count} computers. This changes server access, not Windows startup or a running local backup."
                };
            }
        }
        catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); return Page(); }
        return RedirectToPage();
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Response.Headers["X-LinkSafe-Live"] = "1";
        if (Request.Query["live"] == "1") TempData.Keep();
        Clients = await clientRegistryService.GetAllAsync(cancellationToken);
        Requests = await remoteBackups.GetAllAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostBackupAsync(string domainUser, string computerName, CancellationToken cancellationToken)
    {
        try
        {
            await remoteBackups.EnqueueAsync(domainUser, computerName, cancellationToken);
            Notice = "Backup requested. The agent will scan its selected folders when available. Queued is not completed; progress updates automatically.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Notice = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(string domainUser, string computerName, bool disabled, CancellationToken cancellationToken)
    {
        await clientRegistryService.DisableAsync(domainUser, computerName, disabled, cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string domainUser, string computerName, CancellationToken cancellationToken)
    {
        await clientRegistryService.RemoveAsync(domainUser, computerName, cancellationToken);
        return RedirectToPage();
    }
}
