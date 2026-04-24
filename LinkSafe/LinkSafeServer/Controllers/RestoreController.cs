using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

namespace LinkSafeServer.Controllers;

[ApiController]
[Route("api/restore")]
public sealed class RestoreController(
    RoamingRestoreService roamingRestoreService) : ControllerBase
{
    [HttpGet("options")]
    public async Task<ActionResult<RoamingRestoreOptionsResponse>> GetOptions(
        [FromQuery] string domainUser,
        [FromQuery] string computerName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domainUser) || string.IsNullOrWhiteSpace(computerName))
        {
            return BadRequest();
        }

        var result = await roamingRestoreService.GetOptionsAsync(domainUser, computerName, cancellationToken);
        return Ok(result);
    }

    [HttpGet("package")]
    public async Task<IActionResult> DownloadRestorePackage(
        [FromQuery] string domainUser,
        [FromQuery] string sourceComputerName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domainUser) || string.IsNullOrWhiteSpace(sourceComputerName))
        {
            return BadRequest();
        }

        var archive = await roamingRestoreService.CreateRestoreArchiveAsync(domainUser, sourceComputerName, cancellationToken);
        if (archive is null)
        {
            return NotFound();
        }

        HttpContext.Response.OnCompleted(() =>
        {
            try
            {
                if (System.IO.File.Exists(archive.ArchivePath))
                {
                    System.IO.File.Delete(archive.ArchivePath);
                }
            }
            catch
            {
                // Best-effort cleanup after the response completes.
            }

            return Task.CompletedTask;
        });

        return PhysicalFile(archive.ArchivePath, "application/zip", archive.DownloadName);
    }
}
