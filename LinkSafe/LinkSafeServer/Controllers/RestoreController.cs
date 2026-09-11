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
        CancellationToken cancellationToken,
        [FromQuery] bool includeCurrentComputer = false)
    {
        if (string.IsNullOrWhiteSpace(domainUser) || string.IsNullOrWhiteSpace(computerName))
        {
            return BadRequest();
        }

        var result = await roamingRestoreService.GetOptionsAsync(domainUser, computerName, cancellationToken, includeCurrentComputer);
        return Ok(result);
    }

    [HttpGet("manifest")]
    public async Task<IActionResult> GetManifest([FromQuery] string domainUser, [FromQuery] string sourceComputerName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domainUser) || string.IsNullOrWhiteSpace(sourceComputerName)) return BadRequest();
        try
        {
            var result = await roamingRestoreService.GetManifestAsync(domainUser, sourceComputerName, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex) { return UnprocessableEntity(new { message = ex.Message }); }
        catch (IOException) { return BadRequest(new { message = "The restore source could not be safely inspected." }); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpGet("file")]
    public async Task<IActionResult> GetFile([FromQuery] string domainUser, [FromQuery] string sourceComputerName,
        [FromQuery] string relativePath, [FromQuery] long length, [FromQuery] long lastWriteTicks, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domainUser) || string.IsNullOrWhiteSpace(sourceComputerName) || string.IsNullOrWhiteSpace(relativePath) || length < 0) return BadRequest();
        try
        {
            var stream = await roamingRestoreService.OpenRestoreFileAsync(domainUser, sourceComputerName, relativePath, length, lastWriteTicks, cancellationToken);
            return stream is null ? NotFound() : File(stream, "application/octet-stream");
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (IOException) { return BadRequest(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
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
