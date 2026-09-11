using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LinkSafeServer.Pages.Logs;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IndexModel(LogReaderService logReaderService) : PageModel
{
    public IReadOnlyList<string> Lines { get; private set; } = [];
    public string LogFilePath => logReaderService.LogFilePath;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Response.Headers["X-LinkSafe-Live"] = "1";
        Lines = await logReaderService.ReadRecentLinesAsync(cancellationToken: cancellationToken);
    }

    public IActionResult OnGetDownload()
    {
        if (!System.IO.File.Exists(logReaderService.LogFilePath))
        {
            return NotFound();
        }

        return File(System.IO.File.ReadAllBytes(logReaderService.LogFilePath), "text/plain", Path.GetFileName(logReaderService.LogFilePath));
    }
}
