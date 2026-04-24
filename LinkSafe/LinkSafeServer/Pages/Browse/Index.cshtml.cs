using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Models;

namespace LinkSafeServer.Pages.Browse;

public sealed record DownloadFileResult(string Path, string FileName);

public sealed class IndexModel(BackupStorageService backupStorageService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Username { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string ComputerName { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string RelativePath { get; set; } = string.Empty;

    public IReadOnlyList<BackupBrowseItem> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(ComputerName))
        {
            Items = [];
            return;
        }

        Items = await backupStorageService.BrowseAsync(Username, ComputerName, RelativePath, cancellationToken);
    }

    public async Task<IActionResult> OnGetDownloadAsync(string username, string computerName, string relativePath, CancellationToken cancellationToken)
    {
        var result = await backupStorageService.GetFileForDownloadAsync(username, computerName, relativePath, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return File(System.IO.File.ReadAllBytes(result.Path), "application/octet-stream", result.FileName);
    }
}
