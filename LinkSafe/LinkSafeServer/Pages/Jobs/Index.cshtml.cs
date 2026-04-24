using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Models;

namespace LinkSafeServer.Pages.Jobs;

public sealed class IndexModel(BackupJobService backupJobService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Username { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string ComputerName { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string Status { get; set; } = string.Empty;

    public List<BackupJobRecord> Jobs { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var jobs = await backupJobService.GetAllAsync(cancellationToken);
        Jobs = jobs
            .Where(job => string.IsNullOrWhiteSpace(Username) || job.Username.Contains(Username, StringComparison.OrdinalIgnoreCase))
            .Where(job => string.IsNullOrWhiteSpace(ComputerName) || job.ComputerName.Contains(ComputerName, StringComparison.OrdinalIgnoreCase))
            .Where(job => string.IsNullOrWhiteSpace(Status) || job.Status.Contains(Status, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
