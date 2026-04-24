namespace Shared.Models;

public sealed class RegistrationResponse : OperationResult
{
    public string UserFolderName { get; set; } = string.Empty;
    public string ComputerFolderName { get; set; } = string.Empty;
    public string BackupDestinationId { get; set; } = string.Empty;
    public ClientPolicyResponse Policy { get; set; } = new();
}
