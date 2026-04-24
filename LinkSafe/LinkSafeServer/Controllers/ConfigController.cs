using LinkSafeServer.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

namespace LinkSafeServer.Controllers;

[ApiController]
[Route("api/config")]
public sealed class ConfigController(
    ServerConfigService serverConfigService) : ControllerBase
{
    [HttpGet("client")]
    public async Task<ActionResult<ClientPolicyResponse>> GetClientConfig(
        [FromQuery] string? domainUser,
        [FromQuery] string? computerName,
        [FromServices] ClientRegistryService clientRegistryService,
        CancellationToken cancellationToken)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        RegisteredClientRecord? client = null;
        if (!string.IsNullOrWhiteSpace(domainUser) && !string.IsNullOrWhiteSpace(computerName))
        {
            client = await clientRegistryService.GetAsync(domainUser, computerName, cancellationToken);
        }

        return Ok(serverConfigService.ToClientPolicy(config, client));
    }
}
