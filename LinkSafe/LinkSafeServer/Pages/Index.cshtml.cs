using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Models;

namespace LinkSafeServer.Pages;

public sealed class IndexModel(DashboardService dashboardService) : PageModel
{
    public DashboardSummary Summary { get; private set; } = new();
    public string StorageUsageDisplay { get; private set; } = "0 B";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Summary = await dashboardService.GetSummaryAsync(cancellationToken);
        StorageUsageDisplay = FormatBytes(Summary.StorageUsageBytes);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
