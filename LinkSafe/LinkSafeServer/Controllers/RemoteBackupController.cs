using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

namespace LinkSafeServer.Controllers;

[ApiController]
[Route("api/client/backup-request")]
public sealed class RemoteBackupController(RemoteBackupService service) : ControllerBase
{
    // Dispatch creation is admin-only via the protected Razor POST handler, never this agent endpoint.
    [HttpPost("poll")]
    public async Task<IActionResult> Poll(RemoteBackupPoll poll, CancellationToken token)
    {
        try { return Ok(new { request = await service.PollAsync(poll, token) }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }
}
