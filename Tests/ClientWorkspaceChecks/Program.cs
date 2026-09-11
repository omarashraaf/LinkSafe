using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LinkSafeClientSetup;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkSafe-permission-fixture-" + Guid.NewGuid().ToString("N"));
using var identity = WindowsIdentity.GetCurrent();
var sid = identity.User!.Value;
var count = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); count++; Console.WriteLine("PASS: " + message); }
Check(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "Permission suite runs without elevation");
ClientWorkspace.EnsureCreated(root);
var acl = new DirectoryInfo(root).GetAccessControl();
Check(acl.AreAccessRulesProtected, "Private workspace does not inherit broad group permissions");
var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
var allowed = new[] { sid, "S-1-5-18", "S-1-5-32-544" };
Check(rules.Length == 3 && rules.All(r => allowed.Contains(r.IdentityReference.Value) && r.AccessControlType == AccessControlType.Allow),
    "Only this user, SYSTEM and Administrators are granted access");
Check(rules.All(r => r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)), "Private ACL applies to files and subfolders");
var store = new JsonFileStore<ClientAgentConfig>(Path.Combine(root, "client-agent-config.json"));
await store.SaveAsync(new ClientAgentConfig { ServerUrl = "http://fixture:5055" });
await store.SaveAsync(new ClientAgentConfig { ServerUrl = "http://updated:5055", IsAgentEnabled = true });
Check((await store.LoadAsync(() => new())).IsAgentEnabled, "Standard user can create, replace and read configuration");
foreach (var relative in new[] { "status.json", "Queue/test.zip", "bin/.agent-update.lock", "Logs/backup-agent.log", "run-backup-now.signal", "stop-backup.signal" })
{
    var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    await File.WriteAllTextAsync(file, "first");
    await File.WriteAllTextAsync(file, "updated");
    Check(await File.ReadAllTextAsync(file) == "updated", "Standard-user write access: " + relative);
    File.Delete(file);
    Check(!File.Exists(file), "Standard-user deletion: " + relative);
}
ClientWorkspace.EnsureCreated(root);
Check((await store.LoadAsync(() => new())).IsAgentEnabled, "Repeated initialization preserves saved data");
Check(LinkSafeDefaults.ClientDataRoot.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase), "Runtime client path is in the current profile");
foreach (var path in new[] { LinkSafeDefaults.ClientConfigPath, LinkSafeDefaults.ClientStatusPath, LinkSafeDefaults.ClientQueuePath,
    LinkSafeDefaults.ClientBackupStatePath, LinkSafeDefaults.ClientLogFilePath, LinkSafeDefaults.ClientRestoreDirectory,
    LinkSafeDefaults.ClientRunBackupSignalPath, LinkSafeDefaults.ClientStopBackupSignalPath })
    Check(BackupSourcePaths.IsWithin(path, LinkSafeDefaults.ClientDataRoot), "Client data path is profile-scoped: " + Path.GetFileName(path));
Check(LinkSafeDefaults.ServerConfigPath.StartsWith(@"C:\ProgramData\LinkSafeServer"), "Server paths remain unchanged");
var otherSid = "S-1-5-21-4-5-6-1001";
Check(AgentStartupTask.NameForUser(sid) != AgentStartupTask.NameForUser(otherSid), "Users have distinct task names");
var legacy = Path.Combine(root, "legacy");
var migrated = Path.Combine(root, "new-profile");
Directory.CreateDirectory(legacy);
Directory.CreateDirectory(migrated);
var legacyConfig = Path.Combine(legacy, "client-agent-config.json");
await File.WriteAllTextAsync(legacyConfig, JsonSerializer.Serialize(new ClientAgentConfig
{ ServerUrl = "http://fixture:5055", IsAgentEnabled = true, Registered = true, UserFolderName = "another-user", CustomFolders = [@"D:\Private"] }));
await File.WriteAllTextAsync(Path.Combine(legacy, "queue.json"), "KEEP LEGACY QUEUE");
var readOnlyAcl = new FileSecurity();
readOnlyAcl.SetAccessRuleProtection(true, false);
readOnlyAcl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.Read, AccessControlType.Allow));
new FileInfo(legacyConfig).SetAccessControl(readOnlyAcl);
LegacyClientMigration.SeedServerAddress(legacy, migrated);
var imported = JsonSerializer.Deserialize<ClientAgentConfig>(await File.ReadAllTextAsync(Path.Combine(migrated, "client-agent-config.json")))!;
Check(imported.ServerUrl == "http://fixture:5055", "Can seed the server address from a read-only old config");
Check(!imported.IsAgentEnabled && !imported.Registered && imported.UserFolderName == "" && imported.CustomFolders.Count == 0,
    "Shared legacy identity, enablement and private folder choices are not inherited");
Check(!File.Exists(Path.Combine(migrated, "queue.json")) && await File.ReadAllTextAsync(Path.Combine(legacy, "queue.json")) == "KEEP LEGACY QUEUE",
    "Old queued data is preserved, not copied into another user's queue");
var newConfig = Path.Combine(migrated, "client-agent-config.json");
await File.WriteAllTextAsync(newConfig, "existing user settings");
LegacyClientMigration.SeedServerAddress(legacy, migrated);
Check(await File.ReadAllTextAsync(newConfig) == "existing user settings", "Migration never overwrites current user settings");
var executable = Path.Combine(legacy, "bin", "LinkSafeClientAgent.exe");
var task = AgentStartupTask.Definition(executable, sid);
Check(LegacyClientMigration.IsOwnedLegacyTask(task, sid, executable), "Owned legacy task is recognized");
Check(!LegacyClientMigration.IsOwnedLegacyTask(task, otherSid, executable), "Another user's legacy task is retained");
var rejected = false;
try { LegacyClientMigration.IsOwnedLegacyTask(task, sid, @"C:\Different\agent.exe"); } catch (InvalidOperationException) { rejected = true; }
Check(rejected, "A task targeting another installation is not retired");
using var process = Process.GetCurrentProcess();
Check(LegacyClientMigration.ProcessBelongsToUser(process, sid) && !LegacyClientMigration.ProcessBelongsToUser(process, otherSid), "Process ownership is checked by token SID");
foreach (var missing in new Exception[] { new FileNotFoundException(), new DirectoryNotFoundException(),
    new COMException("Missing task", unchecked((int)0x80070002)), new COMException("Missing task path", unchecked((int)0x80070003)) })
    Check(LegacyClientMigration.FindLegacyTask(() => throw missing) is null, "Already removed task does not block backup: " + missing.GetType().Name);
var sentinel = new object();
Check(ReferenceEquals(LegacyClientMigration.FindLegacyTask(() => sentinel), sentinel), "Existing legacy task remains available for ownership verification");
foreach (var failure in new Exception[] { new UnauthorizedAccessException(), new COMException("Scheduler unavailable", unchecked((int)0x800706BA)), new IOException("Unexpected IO failure") })
{
    var propagated = false;
    try { LegacyClientMigration.FindLegacyTask(() => throw failure); }
    catch (Exception ex) { propagated = ReferenceEquals(ex, failure); }
    Check(propagated, "Real task errors are not suppressed: " + failure.GetType().Name);
}
dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
dynamic? taskFolder = null;
try
{
    scheduler.Connect();
    taskFolder = scheduler.GetFolder(@"\");
    Check(LegacyClientMigration.FindLegacyTask(() => (object)taskFolder.GetTask("LinkSafe-readonly-missing-" + Guid.NewGuid().ToString("N"))) is null,
        "Real Windows missing-task lookup allows subsequent manual backup (read-only probe)");
}
finally
{
    if (taskFolder is not null) Marshal.FinalReleaseComObject(taskFolder);
    Marshal.FinalReleaseComObject(scheduler);
}
Console.WriteLine($"All {count} workspace checks passed as a non-elevated user. Fixture: {root}");
