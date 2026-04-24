namespace Shared.Models;

public sealed class ClientIdentityInfo
{
    public string DomainUser { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
}
