using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LinkSafeServer.Pages.Downloads;

public sealed class IndexModel(DownloadCatalogService downloadCatalogService, ServerConfigService serverConfigService) : PageModel
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
        var config = await serverConfigService.GetAsync(cancellationToken);
        var port = Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var uri) ? uri.Port : 5055;
        var fileName = $"LinkSafeClientSetup-{ServerIp}-p{port}.exe";
        Response.OnCompleted(() =>
        {
            downloadCatalogService.DeleteGeneratedSetup();
            return Task.CompletedTask;
        });

        return File(bytes, item.ContentType, fileName);
    }
}
