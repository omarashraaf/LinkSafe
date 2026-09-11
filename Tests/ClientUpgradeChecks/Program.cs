using Shared.Models;
using Shared.Storage;

var fixture = Path.GetFullPath(Path.Combine("artifacts", "client-upgrade-tests", Guid.NewGuid().ToString("N")));
var bin = Path.Combine(fixture, "bin");
Directory.CreateDirectory(bin);
var count = 0;
void Check(bool success, string label) { if (!success) throw new Exception(label); count++; Console.WriteLine("PASS: " + label); }
Version? VersionOf(string path) => File.Exists(path) && Version.TryParse(File.ReadAllText(path), out var v) ? v : null;
var store = new AgentBinaryStore(bin, VersionOf);
var staged = Path.Combine(bin, "LinkSafeClientAgent.test.download");
await File.WriteAllTextAsync(store.ExecutablePath, "1.0.6.0");
await File.WriteAllTextAsync(staged, "1.0.7.0");
var config = Path.Combine(fixture, "client-agent-config.json");
var queue = Path.Combine(fixture, "Queue");
Directory.CreateDirectory(queue);
await File.WriteAllTextAsync(config, "preserve config");
await File.WriteAllTextAsync(Path.Combine(queue, "pending.zip"), "preserve queued package");
var active = false;
await store.ReplaceAsync(staged, new Version(1, 0, 7, 0), () => { active = true; return Task.CompletedTask; }, () => Task.CompletedTask);
Check(active && store.VersionOf(store.ExecutablePath) == new Version(1, 0, 7, 0), "Replacement activates before old version is discarded");
Check(!Directory.EnumerateFiles(bin, "*.previous.exe").Any(), "Successful update removes the rollback executable");
Check(await File.ReadAllTextAsync(config) == "preserve config" && await File.ReadAllTextAsync(Path.Combine(queue, "pending.zip")) == "preserve queued package", "Configuration and queued backup are untouched");
await File.WriteAllTextAsync(staged, "1.0.8.0");
var stopFailedCalled = false;
var failed = false;
try { await store.ReplaceAsync(staged, new Version(1, 0, 7, 0), () => throw new IOException("test startup failed"), () => { stopFailedCalled = true; return Task.CompletedTask; }); }
catch (IOException) { failed = true; }
Check(failed && stopFailedCalled && store.VersionOf(store.ExecutablePath) == new Version(1, 0, 7, 0), "Failed activation restores previous working binary");
await File.WriteAllTextAsync(staged, "1.0.8.0");
failed = false;
using (var locked = new FileStream(store.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
{
    try { await store.ReplaceAsync(staged, new Version(1, 0, 7, 0), () => Task.CompletedTask, () => Task.CompletedTask); }
    catch (IOException) { failed = true; }
}
Check(failed && store.VersionOf(store.ExecutablePath) == new Version(1, 0, 7, 0), "Locked installed executable is preserved if replacement cannot proceed");
await File.WriteAllTextAsync(staged, "1.0.5.0");
failed = false;
try { await store.ReplaceAsync(staged, new Version(1, 0, 7, 0), () => Task.CompletedTask, () => Task.CompletedTask); }
catch (InvalidOperationException) { failed = true; }
Check(failed && store.VersionOf(store.ExecutablePath) == new Version(1, 0, 7, 0), "Stale server download cannot downgrade or delete installed agent");
await File.WriteAllTextAsync(staged, "not an agent");
failed = false;
try { await store.ReplaceAsync(staged, new Version(1, 0, 7, 0), () => Task.CompletedTask, () => Task.CompletedTask); }
catch (InvalidOperationException) { failed = true; }
Check(failed, "Invalid download is rejected before replacement");
var outside = Path.Combine(fixture, "outside.exe");
await File.WriteAllTextAsync(outside, "1.0.9.0");
failed = false;
try { await store.ReplaceAsync(outside, new Version(1, 0, 7, 0), () => Task.CompletedTask, () => Task.CompletedTask); }
catch (InvalidOperationException) { failed = true; }
Check(failed && File.Exists(outside), "Updater refuses a file outside managed installation");
var old = Path.Combine(bin, $"LinkSafeClientAgent.{Guid.NewGuid():N}.previous.exe");
var newer = Path.Combine(bin, $"LinkSafeClientAgent.{Guid.NewGuid():N}.previous.exe");
var unknown = Path.Combine(bin, "LinkSafeClientAgent.user-copy.previous.exe");
await File.WriteAllTextAsync(old, "1.0.6.0");
await File.WriteAllTextAsync(newer, "1.0.9.0");
await File.WriteAllTextAsync(unknown, "1.0.5.0");
var removed = store.CleanupOlderCopies();
Check(removed.Count == 1 && !File.Exists(old), "Only older LinkSafe-owned rollback copies are cleaned up");
Check(File.Exists(newer) && File.Exists(unknown) && File.Exists(outside), "Newer, unknown and outside executables are preserved");
var candidates = new List<RoamingRestoreCandidate>
{
    new() { SourceDomainUser = "DOMAIN\\user", SourceComputerName = "OLD-PC", AvailableFolders = ["Documents", "AppDataRoaming"] },
    new() { SourceDomainUser = "DOMAIN\\user", SourceComputerName = "NEW-PC", AvailableFolders = ["Documents"] },
    new() { SourceDomainUser = "DOMAIN\\other", SourceComputerName = "OTHER-PC", AvailableFolders = ["Documents"] }
};
var selected = DeviceMigration.OtherDevices(candidates, "domain\\USER", "new-pc");
Check(selected.Count == 1 && selected[0].SourceComputerName == "OLD-PC", "Migration lists only other devices for the same claimed Windows account");
var userRoot = Path.Combine(fixture, "new-profile");
var roots = MissingFileRestore.BuildRoots(new ClientAgentConfig { BackupFolders = ["Documents"] }, name =>
    name switch { "Documents" => Path.Combine(userRoot, "Documents"), "Desktop" => Path.Combine(userRoot, "Desktop"),
        "AppDataRoaming" => Path.Combine(userRoot, "AppData", "Roaming"), "ChromeProfile" => Path.Combine(userRoot, "AppData", "Local", "Google", "Chrome", "User Data"), _ => "" });
Directory.CreateDirectory(roots["Documents"]);
await File.WriteAllTextAsync(Path.Combine(roots["Documents"], "existing.txt"), "local content");
var manifest = new RestoreManifest { Files =
[
    new() { RelativePath = "Documents/existing.txt", Length = 123 },
    new() { RelativePath = "Documents/work/report.txt", Length = 10 },
    new() { RelativePath = "Desktop/shortcut.url", Length = 20 },
    new() { RelativePath = "AppDataRoaming/App/settings.json", Length = 30 },
    new() { RelativePath = "ChromeProfile/Default/Bookmarks", Length = 40 }
] };
var plan = MissingFileRestore.Compare(manifest, "DOMAIN\\user", "OLD-PC", roots);
Check(plan.Existing == 1 && plan.Missing.Count == 4, "Migration preserves existing files and selects all missing supported profile data");
Check(plan.Missing.Single(f => f.File.RelativePath.StartsWith("Documents/")).Destination == Path.Combine(userRoot, "Documents", "work", "report.txt"), "Document goes inside current user's real Documents path");
Check(plan.Missing.Single(f => f.File.RelativePath.StartsWith("AppDataRoaming/")).Destination == Path.Combine(userRoot, "AppData", "Roaming", "App", "settings.json"), "App configuration goes inside current user's AppData path");
Check(plan.Missing.Single(f => f.File.RelativePath.StartsWith("ChromeProfile/")).Destination == Path.Combine(userRoot, "AppData", "Local", "Google", "Chrome", "User Data", "Default", "Bookmarks"), "Browser data maps inside the correct profile, not a device folder");
var preview = DeviceMigration.Preview("DOMAIN\\user", "OLD-PC", plan, roots);
Check(preview.Contains(roots["AppDataRoaming"]) && preview.Contains("NOT be deleted") && preview.Contains("existing local files will be kept"), "Confirmation previews real destinations and non-destructive behavior");
Console.WriteLine($"All {count} upgrade and migration checks passed using isolated fixtures; no running agent or user profile changed.");
