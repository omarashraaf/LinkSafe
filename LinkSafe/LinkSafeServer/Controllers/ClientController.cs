using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

namespace LinkSafeServer.Controllers;

[ApiController]
[Route("api/client")]
public sealed class ClientController(
    ClientRegistryService clientRegistryService) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<RegistrationResponse>> Register(
        [FromBody] RegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var response = await clientRegistryService.RegisterAsync(request, cancellationToken);
        return response.Success ? Ok(response) : BadRequest(response);
    }

    [HttpPost("heartbeat")]
    public async Task<ActionResult<OperationResult>> Heartbeat(
        [FromBody] HeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var response = await clientRegistryService.UpdateHeartbeatAsync(request, cancellationToken);
        return response.Success ? Ok(response) : BadRequest(response);
    }
}
