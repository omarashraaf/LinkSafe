using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LinkSafeServer.Pages.Downloads;

public sealed class IndexModel(DownloadCatalogService downloadCatalogService) : PageModel
{
    [BindProperty]
    public string ServerIp { get; set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
    }

    public async Task<IActionResult> OnPostGenerateAsync(CancellationToken cancellationToken)
    {
        var item = await downloadCatalogService.CreateCustomizedSetupAsync(ServerIp, cancellationToken);
        if (item is null)
        {
            return NotFound("Publish LinkSafeClientSetup.exe to the server downloads folder first.");
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(item.FullPath, cancellationToken);
        Response.OnCompleted(() =>
        {
            downloadCatalogService.DeleteGeneratedSetup();
            return Task.CompletedTask;
        });

        return File(bytes, item.ContentType, item.Name);
    }
}
