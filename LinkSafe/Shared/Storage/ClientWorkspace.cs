using System.Security.AccessControl;
using System.Security.Principal;
using Shared.Constants;

namespace Shared.Storage;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ClientWorkspace
{
    public static void EnsureCreated(string? directory = null)
    {
        var root = Path.GetFullPath(directory ?? LinkSafeDefaults.ClientDataRoot);
        RestorePathSafety.AssertNoLinks(root);
        Directory.CreateDirectory(root);
        using var user = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { user.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
    }
}
