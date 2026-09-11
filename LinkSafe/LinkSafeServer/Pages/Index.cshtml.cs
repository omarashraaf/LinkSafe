using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace LinkSafeServer.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IndexModel(DashboardService dashboardService) : PageModel
{
    public DashboardSummary Summary { get; private set; } = new();
    public string StorageUsageDisplay { get; private set; } = "0 B";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Response.Headers["X-LinkSafe-Live"] = "1";
        Summary = await dashboardService.GetSummaryAsync(cancellationToken);
        StorageUsageDisplay = Summary.StorageUsageState == "Ready" ? FormatBytes(Summary.StorageUsageBytes) : Summary.StorageUsageState;
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
