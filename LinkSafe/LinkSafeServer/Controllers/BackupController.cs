using System.Text.Json;
using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

namespace LinkSafeServer.Controllers;

[ApiController]
[Route("api/backup")]
public sealed class BackupController(
    BackupStorageService backupStorageService,
    ILogger<BackupController> logger) : ControllerBase
{
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
