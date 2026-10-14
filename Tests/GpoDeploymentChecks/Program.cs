using System.Security.Cryptography;
using LinkSafeClientSetup;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "gpo-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
async Task Reject(Func<Task> call, string name)
{
    try { await call(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
    { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
string[] Args(string ip = "10.20.30.40", string port = "5055", string path = @"D:\fixture", string hash = "", string id = "baseline-v1") =>
    ["--gpo-install", "--server-ip", ip, "--port", port, "--package-root", path, "--agent-sha256", hash.Length == 0 ? new string('A', 64) : hash, "--deployment-id", id];
var options = GpoOptions.Parse(Args());
Check(options.ServerUrl == "http://10.20.30.40:5055", "IP-only input builds the configured LAN URL");
Check(GpoOptions.Parse(Args(ip: "2001:db8::5")).ServerUrl == "http://[2001:db8::5]:5055", "IPv6 server URL is bracketed");
foreach (var ip in new[] { "http://10.20.30.40:5055", "127.0.0.1", "0.0.0.0", "::", "255.255.255.255", "bad host" })
    await Reject(() => { GpoOptions.Parse(Args(ip: ip)); return Task.CompletedTask; }, "Unsafe/invalid server rejected: " + ip);
await Reject(() => { GpoOptions.Parse(Args(port: "0")); return Task.CompletedTask; }, "Invalid port rejected");
await Reject(() => { GpoOptions.Parse(Args(path: @"\\server\share")); return Task.CompletedTask; }, "Executable payload must be cached locally");
await Reject(() => { GpoOptions.Parse(Args(hash: "bad")); return Task.CompletedTask; }, "Invalid package hash rejected");
await Reject(() => { GpoOptions.Parse(Args(id: "../../escape")); return Task.CompletedTask; }, "Invalid deployment ID rejected");
await Reject(() => { GpoOptions.Parse([.. Args(), "--extra", "x"]); return Task.CompletedTask; }, "Unknown arguments rejected without UI");
var drives = new[]
{
    new GpoDrive(@"C:\", DriveType.Fixed, true), new GpoDrive(@"D:\", DriveType.Fixed, true),
    new GpoDrive(@"d:\", DriveType.Fixed, true), new GpoDrive(@"E:\", DriveType.Fixed, false),
    new GpoDrive(@"F:\", DriveType.Removable, true), new GpoDrive(@"G:\", DriveType.Network, true),
    new GpoDrive(@"H:\", DriveType.CDRom, true), new GpoDrive(@"I:\", DriveType.Fixed, true),
    new GpoDrive(@"J:\sub", DriveType.Fixed, true), new GpoDrive(@"K:\", DriveType.Unknown, true)
};
Check(GpoDeployment.SelectDrives(drives, @"I:\").SequenceEqual(new[] { @"D:\" }), "Only ready non-C non-system fixed roots, deduplicated");
Check(GpoDeployment.SelectDrives([], @"C:\").Count == 0, "No extra drives still permits user folder backup");
var prepared = 0;
var started = 0;
var reportedPause = false;
Task Prepare() { prepared++; return Task.CompletedTask; }
Task Start(bool paused) { started++; reportedPause = paused; return Task.CompletedTask; }
var stages = new List<string>();
var result = await GpoDeployment.ApplyAsync(root, options, drives, @"I:\", Prepare, Start, stages.Add);
Check(stages.SequenceEqual(new[] { "Reading deployment state", "Preparing installation", "Saving baseline configuration",
    "Requesting initial backup", "Starting installed applications", "Saving deployment receipt",
    "Deployment complete; backup completion not verified" }), "Ordered diagnostic stages distinguish installation from backup completion");
var configStore = new JsonFileStore<ClientAgentConfig>(Path.Combine(root, "client-agent-config.json"));
var config = await configStore.ReadAsync(() => new());
var policy = new ClientPolicyStore(root);
var selection = await policy.GetSelectionAsync();
Check(config.IsAgentEnabled && config.ServerUrl == options.ServerUrl, "Fresh profile enabled with correct server");
Check(selection.BackupFolders.SequenceEqual(LinkSafeDefaults.DefaultBackupFolders), "All current default user/app/browser folders selected");
Check(selection.OverrideServerFolders && selection.CustomFolders.SequenceEqual(new[] { @"D:\" }), "Deployment creates exact local baseline override");
var request = await File.ReadAllTextAsync(Path.Combine(root, "run-backup-now.signal"));
Check(Guid.TryParse(request, out _) && started == 1 && prepared == 1 && !reportedPause, "First backup requested and startup invoked once");
Check(result.Contains("requested") && !File.Exists(Path.Combine(root, "status.json")), "Deployment never fabricates completed backup status");
config.IsAgentEnabled = false;
await configStore.SaveAsync(config);
await policy.SaveSelectionAsync(new() { ServerUrl = options.ServerUrl, OverrideServerFolders = true, BackupFolders = ["Documents"] });
await File.WriteAllTextAsync(Path.Combine(root, "stop-backup.signal"), "USER-PAUSE");
stages.Clear();
await GpoDeployment.ApplyAsync(root, options, drives, @"C:\", Prepare, Start, stages.Add);
Check(stages.SequenceEqual(new[] { "Reading deployment state", "Already applied; no changes made" }), "Idempotent deployment logs no new installation or backup");
Check(prepared == 1 && started == 1 && !(await configStore.ReadAsync(() => new())).IsAgentEnabled, "Same ID is idempotent and does not re-enable later-disabled users");
Check((await policy.GetSelectionAsync()).BackupFolders.SequenceEqual(new[] { "Documents" }), "Repeated logon preserves later folder choice");
Check(await File.ReadAllTextAsync(Path.Combine(root, "run-backup-now.signal")) == request, "Repeated deployment does not steal pending request ID");
await GpoDeployment.ApplyAsync(root, options with { DeploymentId = "baseline-v2" }, drives, @"I:\", Prepare, Start);
Check((await policy.GetSelectionAsync()).BackupFolders.Count == LinkSafeDefaults.DefaultBackupFolders.Length, "New baseline explicitly applies defaults to an existing profile once");
Check(reportedPause && await File.ReadAllTextAsync(Path.Combine(root, "stop-backup.signal")) == "USER-PAUSE", "Existing Stop backup signal preserved, never silently resumed");
Check((await configStore.ReadAsync(() => new())).IsAgentEnabled, "New baseline enables existing installation");
await Reject(() => GpoDeployment.ApplyAsync(root, options with { ServerUrl = "http://10.20.30.41:5055", DeploymentId = "baseline-v2" }, drives, @"C:\", Prepare, Start), "Receipt cannot silently switch servers");
await Reject(() => GpoDeployment.ApplyAsync(root, options with { ServerUrl = "http://10.20.30.41:5055", DeploymentId = "baseline-v3" }, drives, @"C:\", Prepare, Start), "A new ID cannot silently redirect an existing profile either");
var failed = Path.Combine(root, "failure");
stages.Clear();
await Reject(() => GpoDeployment.ApplyAsync(failed, options, drives, @"C:\", () => throw new IOException("Fixture task denied"), Start, stages.Add), "Task/preparation errors propagate");
Check(stages.Last() == "Preparing installation" && !stages.Any(s => s.StartsWith("Deployment complete")), "Failed preparation retains diagnostic checkpoint without false completion");
Check(!File.Exists(Path.Combine(failed, "client-agent-config.json")) && !File.Exists(Path.Combine(failed, "gpo-deployment.json")), "Preparation failure creates no false receipt or enabled config");
stages.Clear();
await Reject(() => GpoDeployment.ApplyAsync(failed, options, drives, @"C:\", Prepare, _ => throw new IOException("Fixture process start failed"), stages.Add), "Process startup error propagates");
Check(stages.Last() == "Starting installed applications" && !stages.Contains("Saving deployment receipt"), "Failed startup leaves last stage and no success checkpoint");
Check(!File.Exists(Path.Combine(failed, "gpo-deployment.json")), "Startup failure remains retryable");
await GpoDeployment.ApplyAsync(failed, options, drives, @"C:\", Prepare, Start);
Check(File.Exists(Path.Combine(failed, "gpo-deployment.json")), "Failed deployment can complete safely on next attempt");
var existing = Path.Combine(root, "existing-profile");
Directory.CreateDirectory(existing);
var existingConfig = new JsonFileStore<ClientAgentConfig>(Path.Combine(existing, "client-agent-config.json"));
await existingConfig.SaveAsync(new() { ServerUrl = options.ServerUrl, Registered = true,
    ClientId = "fixture-client", UserFolderName = "fixture-user", IsAgentEnabled = false });
await File.WriteAllTextAsync(Path.Combine(existing, "queue.json"), "RETAIN QUEUE");
await File.WriteAllTextAsync(Path.Combine(existing, "backup-state.json"), "RETAIN CONFIRMED STATE");
await new ClientPolicyStore(existing).SaveSelectionAsync(new() { ServerUrl = options.ServerUrl,
    OverrideServerFolders = true, BackupFolders = ["Music"], CustomFolders = [@"Z:\OldSelection"] });
await GpoDeployment.ApplyAsync(existing, options, drives, @"I:\", Prepare, Start);
var existingAfter = await existingConfig.ReadAsync(() => new());
Check(existingAfter.Registered && existingAfter.ClientId == "fixture-client" && existingAfter.UserFolderName == "fixture-user", "Existing identity and registration retained on first GPO application");
Check((await new ClientPolicyStore(existing).GetSelectionAsync()).CustomFolders.SequenceEqual(new[] { @"D:\" }), "First GPO application replaces existing folder selection, not just fresh profiles");
Check(await File.ReadAllTextAsync(Path.Combine(existing, "queue.json")) == "RETAIN QUEUE" &&
    await File.ReadAllTextAsync(Path.Combine(existing, "backup-state.json")) == "RETAIN CONFIRMED STATE", "Existing queue and backup state retained exactly");
var badExe = Path.Combine(root, "bad.exe");
var approved = Path.Combine(root, "approved-payload.bin");
var installedPayload = Path.Combine(root, "installed-payload.bin");
await File.WriteAllTextAsync(approved, "verified fixture bytes");
File.Copy(approved, installedPayload);
GpoDeployment.VerifySamePayload(approved, installedPayload);
Check(true, "Byte-identical installed payload can be reused");
GpoDeployment.VerifySamePayload(approved, approved);
Check(true, "Running from the approved installed path is supported");
await File.WriteAllTextAsync(installedPayload, "different fixture bytes");
await Reject(() => { GpoDeployment.VerifySamePayload(approved, installedPayload); return Task.CompletedTask; }, "Changed installed payload is rejected before reuse");
Check(await File.ReadAllTextAsync(installedPayload) == "different fixture bytes", "Integrity rejection does not delete or overwrite installed files");
await Reject(() => { GpoDeployment.VerifySamePayload(approved, Path.Combine(root, "missing-payload.bin")); return Task.CompletedTask; }, "Missing installed payload fails verification");
await File.WriteAllTextAsync(badExe, "not an executable");
await Reject(() => { GpoDeployment.VerifyAgent(badExe, new string('0', 64)); return Task.CompletedTask; }, "Hash mismatch blocks payload");
var matchingHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(badExe)));
await Reject(() => { GpoDeployment.VerifyAgent(badExe, matchingHash); return Task.CompletedTask; }, "Matching hash alone does not accept an unrecognized executable");
Console.WriteLine($"All {count} GPO deployment checks passed. Fixture: {root}. No real tasks, processes, profile settings or backups changed.");
