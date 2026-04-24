namespace Shared.Models;

public sealed class RoamingRestoreOptionsResponse
{
    public string DomainUser { get; set; } = string.Empty;
    public string CurrentComputerName { get; set; } = string.Empty;
    public List<RoamingRestoreCandidate> Candidates { get; set; } = [];
}
