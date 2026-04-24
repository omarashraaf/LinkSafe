using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Constants;
using Shared.Models;

namespace LinkSafeServer.Pages.Clients;

public sealed class DetailsModel(
    ClientRegistryService clientRegistryService,
    ServerConfigService serverConfigService) : PageModel
{
    public RegisteredClientRecord? Client { get; private set; }
    public string[] FolderOptions => LinkSafeDefaults.KnownFolders;

    [BindProperty]
    public List<string> SelectedFolders { get; set; } = [];

    public async Task OnGetAsync(string domainUser, string computerName, CancellationToken cancellationToken)
    {
        Client = await clientRegistryService.GetAsync(domainUser, computerName, cancellationToken);
        if (Client is null)
        {
            return;
        }

        var config = await serverConfigService.GetAsync(cancellationToken);
        SelectedFolders = Client.BackupFoldersOverride.Count > 0
            ? [.. Client.BackupFoldersOverride]
            : [.. config.BackupFolders];
    }

    public async Task<IActionResult> OnPostAsync(string domainUser, string computerName, CancellationToken cancellationToken)
    {
        await clientRegistryService.SaveClientPolicyAsync(
            domainUser,
            computerName,
            SelectedFolders.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            [],
            cancellationToken);

        return RedirectToPage(new { domainUser, computerName });
    }
}
