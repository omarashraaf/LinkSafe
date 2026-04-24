using LinkSafeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinkSafeServer.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/downloads")]
public sealed class PublicDownloadsController(DownloadCatalogService downloadCatalogService) : ControllerBase
{
    [HttpGet("client-agent")]
    public IActionResult DownloadClientAgent()
    {
        var item = downloadCatalogService.GetClientAgent();
        if (item is null)
        {
            return NotFound();
        }

        return File(System.IO.File.ReadAllBytes(item.FullPath), item.ContentType, item.Name);
    }
}
