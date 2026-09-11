using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeServer.Pages.Clients;

public sealed class DetailsModel(
    ClientRegistryService clientRegistryService,
    ServerConfigService serverConfigService) : PageModel
{
    public RegisteredClientRecord? Client { get; private set; }
    public string[] FolderOptions => LinkSafeDefaults.KnownFolders;

    [BindProperty]
    public List<string> SelectedFolders { get; set; } = [];
    [BindProperty]
    public List<string> SelectedDrives { get; set; } = [];
    public bool UseServerDefaults { get; private set; }
    [BindProperty]
    public string? CustomFoldersText { get; set; }
    public List<string> DriveOptions => (Client?.AvailableDrives ?? [])
        .Concat(SelectedDrives).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    public async Task OnGetAsync(string domainUser, string computerName, CancellationToken cancellationToken)
    {
        Client = await clientRegistryService.GetAsync(domainUser, computerName, cancellationToken);
        if (Client is null)
        {
            return;
        }

        var config = await serverConfigService.GetAsync(cancellationToken);
        var policy = serverConfigService.ToClientPolicy(config, Client);
        UseServerDefaults = !policy.IsClientOverride;
        SelectedFolders = [.. policy.BackupFolders];
        SelectedDrives = policy.CustomFolders.Where(path => BackupSourcePaths.TryNormalize(path, out var normalized) && normalized.Length == 3).ToList();
        CustomFoldersText = string.Join(Environment.NewLine, policy.CustomFolders.Except(SelectedDrives));
    }

    public async Task<IActionResult> OnPostAsync(string domainUser, string computerName, CancellationToken cancellationToken)
    {
        Client = await clientRegistryService.GetAsync(domainUser, computerName, cancellationToken);
        if (Client is null) return NotFound();
        var customFolders = new List<string>();
        foreach (var input in SelectedDrives.Concat((CustomFoldersText ?? string.Empty)
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!BackupSourcePaths.TryNormalize(input, out var normalized))
                ModelState.AddModelError(nameof(CustomFoldersText), $"Invalid path: {input}. Use a local path such as D:\\ or E:\\Projects, without wildcards or '..'.");
            else
                customFolders.Add(normalized);
        }
        if (!ModelState.IsValid) return Page();
        await clientRegistryService.SaveClientPolicyAsync(
            domainUser,
            computerName,
            SelectedFolders.Where(item => FolderOptions.Contains(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            customFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cancellationToken);

        return RedirectToPage(new { domainUser, computerName, saved = "true" });
    }

    public async Task<IActionResult> OnPostDefaultsAsync(string domainUser, string computerName, CancellationToken cancellationToken)
    {
        if (await clientRegistryService.GetAsync(domainUser, computerName, cancellationToken) is null) return NotFound();
        await clientRegistryService.SaveClientPolicyAsync(domainUser, computerName, [], [], cancellationToken, useServerDefaults: true);
        return RedirectToPage(new { domainUser, computerName, saved = "true" });
    }
}
