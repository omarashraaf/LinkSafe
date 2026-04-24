using Microsoft.AspNetCore.Mvc;
using Shared.Models;

namespace LinkSafeServer.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public ActionResult<HealthResponse> GetHealth() =>
        Ok(new HealthResponse
        {
            ServerName = Environment.MachineName,
            Version = "1.0.0",
            Time = DateTimeOffset.UtcNow
        });
}
