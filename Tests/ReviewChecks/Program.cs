using System.IO.Compression;
using System.Reflection;
using LinkSafeServer.Services;
using LinkSafeClientAgent.Services;
using Shared.Models;
using Shared.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;

// Run after building LinkSafe.slnx. All file fixtures stay under this test output.
var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var failures = 0;
void Check(string name, bool passed)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
    if (!passed) failures++;
}
object? Call(Type type, string method, params object[] args) =>
    type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
bool Accepts(string entryName)
{
    using var stream = new MemoryStream();
    using (var writer = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        writer.CreateEntry(entryName);
    stream.Position = 0;
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    try
    {
        Call(typeof(BackupStorageService), "ValidateArchiveEntries", archive, Path.Combine(fixture, "PC"));
        return true;
    }
    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { return false; }
}
Check("Normal backup entry accepted", Accepts("Documents/report.txt"));
Check("Parent traversal rejected", !Accepts("../../escape.txt"));
Check("Sibling prefix traversal rejected", !Accepts("../PC-other/escape.txt"));
Check("Absolute Windows path rejected", !Accepts("C:/Windows/escape.txt"));

var time = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
var state = new Dictionary<string, BackupFileStateEntry>
{
    ["file.txt"] = new() { SourcePath = "file.txt", Length = 3, LastWriteTimeUtc = time }
};
bool Changed(string name, long size, DateTime modified) => (bool)Call(typeof(BackupPackageBuilder),
    "IsNewOrChanged", new BackupFileStateEntry { SourcePath = name, Length = size, LastWriteTimeUtc = modified }, state)!;
Check("New file detected", Changed("new.txt", 3, time));
Check("Unchanged file skipped", !Changed("file.txt", 3, time));
Check("Modified file detected", Changed("file.txt", 3, time.AddSeconds(1)));
foreach (var year in new[] { 1970, 2200 })
{
    var clamped = (DateTimeOffset)Call(typeof(BackupPackageBuilder), "ClampZipTimestamp", new DateTime(year, 1, 1))!;
    using var zip = new ZipArchive(new MemoryStream(), ZipArchiveMode.Create);
    zip.CreateEntry("date.txt").LastWriteTime = clamped;
    Check($"ZIP timestamp {year} safely clamped", clamped.Year >= 1980 && clamped.Year <= 2107);
}

var destination = Path.Combine(fixture, "report.txt");
File.WriteAllText(destination, "OLD");
File.SetLastWriteTimeUtc(destination, time);
using (var stream = new MemoryStream())
{
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
        var entry = zip.CreateEntry("Documents/report.txt");
        entry.LastWriteTime = new DateTimeOffset(time.AddSeconds(2));
        using var writer = new StreamWriter(entry.Open());
        writer.Write("NEW");
    }
    stream.Position = 0;
    using var zipRead = new ZipArchive(stream, ZipArchiveMode.Read);
    File.SetLastWriteTimeUtc(destination, zipRead.Entries[0].LastWriteTime.UtcDateTime.AddSeconds(-2));
    var skipped = (bool)Call(typeof(BackupStorageService), "IsExistingFileAlreadyCurrent", destination, zipRead.Entries[0])!;
    Check("Server accepts changed same-size file within two seconds", !skipped);
}

var store = new JsonFileStore<StatusFileModel>(Path.Combine(fixture, "status.json"));
await store.SaveAsync(new StatusFileModel { LastOperationStatus = "Packaging" });
var uiSnapshot = await store.LoadAsync(() => new());
var workerSnapshot = await store.LoadAsync(() => new());
workerSnapshot.LastOperationStatus = "Success";
await store.SaveAsync(workerSnapshot);
uiSnapshot.ServerReachable = true;
await store.SaveAsync(uiSnapshot);
Check("UI refresh preserves newer agent status", (await store.LoadAsync(() => new())).LastOperationStatus == "Success");

// Substitute every persistent store before calling real server services.
void Isolate(object service, string field, object isolatedStore) => service.GetType()
    .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, isolatedStore);
var configService = new ServerConfigService(NullLogger<ServerConfigService>.Instance);
Isolate(configService, "_store", new JsonFileStore<ServerConfig>(Path.Combine(fixture, "server.json")));
await configService.SaveAsync(new ServerConfig { BackupRoot = Path.Combine(fixture, "backups") });
var security = new StorageSecurityService(NullLogger<StorageSecurityService>.Instance);
var sanitizer = new PathSanitizer();
var registry = new ClientRegistryService(configService, security, sanitizer, NullLogger<ClientRegistryService>.Instance);
Isolate(registry, "_clientsStore", new JsonFileStore<List<RegisteredClientRecord>>(Path.Combine(fixture, "clients.json")));
Isolate(registry, "_userMapStore", new JsonFileStore<Dictionary<string, string>>(Path.Combine(fixture, "map.json")));
var jobs = new BackupJobService();
Isolate(jobs, "_store", new JsonFileStore<List<BackupJobRecord>>(Path.Combine(fixture, "jobs.json")));
var storage = new BackupStorageService(configService, registry, jobs, security, sanitizer, NullLogger<BackupStorageService>.Instance);
var registration = await registry.RegisterAsync(new RegistrationRequest
{
    DomainUser = "REVIEW\\tester", Username = "tester", Domain = "REVIEW", ComputerName = "PC"
});
Check("Isolated client registration", registration.Success);
async Task<BackupUploadResponse> Upload(string name, string contents)
{
    using var stream = new MemoryStream();
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(contents);
    }
    stream.Position = 0;
    return await storage.ProcessUploadAsync(new FormFile(stream, 0, stream.Length, "file", "test.zip"),
        new BackupUploadMetadata { DomainUser = "REVIEW\\tester", Username = "tester", ComputerName = "PC", FileCount = 1 }, default);
}
var first = await Upload("Documents/test.txt", "first");
var restoredPath = Path.Combine(fixture, "backups", "tester", "PC", "Documents", "test.txt");
Check("Isolated upload extracts exact file bytes", first.Success && File.ReadAllText(restoredPath) == "first");
var second = await Upload("Documents/test.txt", "updated text");
Check("Changed upload replaces file and creates a version", second.Success && second.FilesVersioned == 1 && File.ReadAllText(restoredPath) == "updated text");
var unsafeUpload = await Upload("../../escape.txt", "blocked");
Check("Unsafe package quarantined", !unsafeUpload.Success && Directory.GetFiles(Path.Combine(fixture, "backups", "_Quarantine")).Length == 1);
var failuresBeforeDriveChecks = failures;
Check("Drive root normalizes", BackupSourcePaths.TryNormalize("d:/", out var normalizedDrive) && normalizedDrive == @"D:\");
Check("Whole drive has stable archive root", BackupSourcePaths.ArchiveRoot(@"D:\") == "Drives/D");
Check("Specific folder retains drive and hierarchy", BackupSourcePaths.ArchiveRoot(@"E:\Accounts\Reports") == "Drives/E/Accounts/Reports");
Check("Same folder names on different drives remain distinct", BackupSourcePaths.ArchiveRoot(@"D:\Accounts") != BackupSourcePaths.ArchiveRoot(@"E:\Accounts"));
Check("Parent paths rejected", !BackupSourcePaths.TryNormalize(@"D:\..\Windows", out _));
Check("Network shares rejected", !BackupSourcePaths.TryNormalize(@"\\server\share", out _));
Check("Drive relative paths rejected", !BackupSourcePaths.TryNormalize(@"D:Projects", out _));
Check("Alternate data streams rejected", !BackupSourcePaths.TryNormalize(@"D:\file:stream", out _));
Check("Sibling prefix not excluded", !BackupSourcePaths.IsWithin(@"E:\BackupsOther\test", @"E:\Backups"));
Check("Backup subtree excluded", BackupSourcePaths.IsWithin(@"E:\Backups\user\test", @"E:\Backups"));
var additionalConfig = new ClientAgentConfig { BackupFolders = [], CustomFolders = [Path.GetPathRoot(fixture)!] };
var mappedDrives = new FolderMappingService().GetBackupFolders(additionalConfig);
Check("Whole local drive maps without empty key", mappedDrives.ContainsKey(BackupSourcePaths.ArchiveRoot(Path.GetPathRoot(fixture)!)));
additionalConfig.ProtectedSourcePaths = [@"E:\ServerBackup"];
Check("Server backup data excluded by package builder", (bool)Call(typeof(BackupPackageBuilder), "IsExcludedByDirectoryRule", additionalConfig, @"E:\ServerBackup\user\file.txt")!);
Check("Queue data excluded by package builder", (bool)Call(typeof(BackupPackageBuilder), "IsExcludedByDirectoryRule", additionalConfig, @"C:\ProgramData\LinkSafeAgent\Queue\test.zip")!);
Check("Recycle bin excluded by package builder", (bool)Call(typeof(BackupPackageBuilder), "IsExcludedByDirectoryRule", additionalConfig, @"D:\$RECYCLE.BIN\file.txt")!);
var details = new LinkSafeServer.Pages.Clients.DetailsModel(registry, configService)
{
    SelectedFolders = ["Documents"], SelectedDrives = [@"D:\"], CustomFoldersText = "E:\\Accounts\nE:\\Accounts"
};
await details.OnPostAsync("REVIEW\\tester", "PC", default);
var selectedClient = await registry.GetAsync("REVIEW\\tester", "PC");
var policy = configService.ToClientPolicy(await configService.GetAsync(), selectedClient);
Check("Details form saves drive and deduplicated custom folder", policy.CustomFolders.SequenceEqual(new[] { @"D:\", @"E:\Accounts" }));
Check("Policy carries protected server storage path", policy.ProtectedSourcePaths.Contains(Path.Combine(fixture, "backups")));
details.CustomFoldersText = @"..\invalid";
await details.OnPostAsync("REVIEW\\tester", "PC", default);
Check("Invalid form does not overwrite saved policy", !details.ModelState.IsValid && (await registry.GetAsync("REVIEW\\tester", "PC"))!.CustomFoldersOverride.Count == 2);
await registry.SaveClientPolicyAsync("REVIEW\\tester", "PC", [], [], default);
var clearedPolicy = configService.ToClientPolicy(await configService.GetAsync(), await registry.GetAsync("REVIEW\\tester", "PC"));
Check("Cleared client selections stay empty instead of reverting to defaults", clearedPolicy.BackupFolders.Count == 0 && clearedPolicy.CustomFolders.Count == 0);
var extraUpload = await Upload("Drives/E/Accounts/report.txt", "drive file");
Check("Server stores drive data in separate tree", extraUpload.Success && File.ReadAllText(Path.Combine(fixture, "backups", "tester", "PC", "Drives", "E", "Accounts", "report.txt")) == "drive file");
Console.WriteLine($"Drive feature failures: {failures - failuresBeforeDriveChecks}");
var tracker = new DriveInventoryTracker();
var reports = 0;
Task<bool> Report() { reports++; return Task.FromResult(true); }
await tracker.RefreshAsync("http://server", [@"C:\", @"D:\"], Report);
Check("Inventory reports at startup", reports == 1);
await tracker.RefreshAsync("http://server", [@"d:\", @"c:\"], Report);
Check("Unchanged inventory ignores ordering and case", reports == 1);
await tracker.RefreshAsync("http://server", [@"C:\"], Report);
Check("Drive removal reported", reports == 2);
await tracker.RefreshAsync("http://server", [@"C:\", @"D:\"], Report);
Check("Reconnected drive reported", reports == 3);
await tracker.RefreshAsync("http://server", [], () => Task.FromResult(false));
await tracker.RefreshAsync("http://server", [], Report);
Check("Failed inventory retried and empty inventory reported", reports == 4);
await tracker.RefreshAsync("http://other-server", [], Report);
Check("Server change forces inventory report", reports == 5);
Console.WriteLine($"Failures: {failures}. Fixture directory: {fixture}");
return failures == 0 ? 0 : 1;
