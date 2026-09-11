using System.Text.Json;
using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

namespace LinkSafeServer.Controllers;

[ApiController]
[Route("api/backup")]
public sealed class BackupController(
    BackupStorageService backupStorageService,
    BackupPresenceService backupPresenceService,
    ILogger<BackupController> logger) : ControllerBase
{
    [HttpPost("check")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult<BackupPresenceResponse>> Check(BackupPresenceRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await backupPresenceService.CheckAsync(request, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { message = "Cannot verify backup storage access or client registration." }); }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Backup presence check could not inspect storage.");
            return StatusCode(503, new { message = "Backup storage could not be checked. Try again after checking server storage." });
        }
    }

    [HttpPost("upload")]
    [RequestSizeLimit(1_100L * 1024L * 1024L)]
    [RequestFormLimits(MultipartBodyLengthLimit = 1_100L * 1024L * 1024L)]
    public async Task<ActionResult<BackupUploadResponse>> Upload(CancellationToken cancellationToken)
    {
        var form = await Request.ReadFormAsync(cancellationToken);
        var metadataJson = form["metadata"].FirstOrDefault();
        var file = form.Files["file"];

        if (string.IsNullOrWhiteSpace(metadataJson) || file is null)
        {
            return BadRequest(new BackupUploadResponse { Success = false, Message = "Missing metadata or file." });
        }

        BackupUploadMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<BackupUploadMetadata>(metadataJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Upload metadata could not be parsed.");
            return BadRequest(new BackupUploadResponse { Success = false, Message = "Invalid metadata JSON." });
        }

        if (metadata is null)
        {
            return BadRequest(new BackupUploadResponse { Success = false, Message = "Invalid metadata." });
        }

        var result = await backupStorageService.ProcessUploadAsync(file, metadata, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
