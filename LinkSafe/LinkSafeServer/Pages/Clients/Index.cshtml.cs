using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Models;

namespace LinkSafeServer.Pages.Clients;

public sealed class IndexModel(ClientRegistryService clientRegistryService) : PageModel
{
    public List<RegisteredClientRecord> Clients { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Clients = await clientRegistryService.GetAllAsync(cancellationToken);
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
