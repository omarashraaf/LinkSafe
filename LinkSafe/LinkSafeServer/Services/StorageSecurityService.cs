using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using Shared.Models;

namespace LinkSafeServer.Services;

[SupportedOSPlatform("windows")]
public sealed class StorageSecurityService(ILogger<StorageSecurityService> logger)
{
    public Task EnsureStorageStructureAsync(ServerConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var folder in new[]
                 {
                     config.BackupRoot,
                     config.IncomingRoot,
                     config.QuarantineRoot,
                     config.VersionsRoot,
                     config.LogsRoot
                 })
        {
            Directory.CreateDirectory(folder);
        }

        return Task.CompletedTask;
    }

    public async Task<OperationResult> ApplySecurePermissionsAsync(ServerConfig config, CancellationToken cancellationToken = default)
    {
        await EnsureStorageStructureAsync(config, cancellationToken);

        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            AddFullControlRule(security, "Administrators");
            AddFullControlRule(security, "SYSTEM");

            if (!string.IsNullOrWhiteSpace(config.ServiceAccount))
            {
                AddFullControlRule(security, config.ServiceAccount);
            }

            if (!string.IsNullOrWhiteSpace(config.BackupOperatorsGroup))
            {
                AddFullControlRule(security, config.BackupOperatorsGroup);
            }

            var directory = new DirectoryInfo(config.BackupRoot);
            directory.SetAccessControl(security);

            logger.LogInformation("Applied secure NTFS permissions to {BackupRoot}.", config.BackupRoot);
            return OperationResult.Ok("Secure storage permissions applied successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply secure NTFS permissions.");
            return OperationResult.Fail($"Failed to apply secure permissions: {ex.Message}");
        }
    }

    private static void AddFullControlRule(DirectorySecurity security, string identity)
    {
        var rule = new FileSystemAccessRule(
            new NTAccount(identity),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

        security.AddAccessRule(rule);
    }
}
