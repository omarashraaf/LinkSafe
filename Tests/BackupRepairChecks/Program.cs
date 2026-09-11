using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using LinkSafeClientAgent.Services;
using LinkSafeServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Models;
using Shared.Storage;

var fixture = Path.GetFullPath(Path.Combine("artifacts", "backup-repair-tests", Guid.NewGuid().ToString("N")));
var local = Path.Combine(fixture, "profile", "Documents");
var backup = Path.Combine(fixture, "server");
Directory.CreateDirectory(local);
Directory.CreateDirectory(backup);
var count = 0;
void Check(bool success, string label) { if (!success) throw new Exception(label); count++; Console.WriteLine("PASS: " + label); }
void Inject(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
var configService = new ServerConfigService(NullLogger<ServerConfigService>.Instance);
Inject(configService, "_store", new JsonFileStore<ServerConfig>(Path.Combine(fixture, "config.json")));
await configService.SaveAsync(new ServerConfig { BackupRoot = backup });
var security = new StorageSecurityService(NullLogger<StorageSecurityService>.Instance);
var sanitizer = new PathSanitizer();
var registry = new ClientRegistryService(configService, security, sanitizer, NullLogger<ClientRegistryService>.Instance);
var clients = new JsonFileStore<List<RegisteredClientRecord>>(Path.Combine(fixture, "clients.json"));
Inject(registry, "_clientsStore", clients);
var identity = new ClientIdentityService().GetCurrent();
var clientRecord = new RegisteredClientRecord { DomainUser = identity.DomainUser, Username = identity.Username,
    ComputerName = identity.ComputerName, UserFolderName = "user", ComputerFolderName = "PC1", BackupDestinationPath = "unused-cached-path" };
await clients.SaveAsync([clientRecord]);
var jobs = new BackupJobService();
Inject(jobs, "_store", new JsonFileStore<List<BackupJobRecord>>(Path.Combine(fixture, "jobs.json")));
var storage = new BackupStorageService(configService, registry, jobs, security, sanitizer, NullLogger<BackupStorageService>.Instance);
var presence = new BackupPresenceService(configService, registry);
var handler = new PresenceHandler(presence);
var api = new ClientApiService(new FixtureFactory(handler));
var config = new ClientAgentConfig { ServerUrl = "http://fixture", BackupFolders = ["Documents"] };
var builder = new BackupPackageBuilder(new FolderMappingService(name => name == "Documents" ? local : ""),
    new ClientIdentityService(), NullLogger<BackupPackageBuilder>.Instance, Path.Combine(fixture, "queue"));
for (var i = 0; i < 10; i++) await File.WriteAllTextAsync(Path.Combine(local, $"file-{i}.txt"), "original contents " + i);
var state = new Dictionary<string, BackupFileStateEntry>(StringComparer.OrdinalIgnoreCase);
async Task Upload(QueuePackageRecord package)
{
    await using var stream = File.OpenRead(package.FilePath);
    var result = await storage.ProcessUploadAsync(new FormFile(stream, 0, stream.Length, "file", "backup.zip"),
        JsonSerializer.Deserialize<BackupUploadMetadata>(package.MetadataJson)!, default);
    Check(result.Success, "Real server extraction succeeds");
    foreach (var entry in package.IncludedFiles) state[entry.SourcePath] = entry;
}
async Task<Dictionary<string, BackupFileStateEntry>> Verify() => await api.VerifyBackupStateAsync(config, identity, state, null, default);
var initial = await builder.CreateSmartBackupAsync(config, null, state, [], default) ?? throw new Exception("No initial package");
await Upload(initial);
Check((await Verify()).Count == 10, "Existing files are verified using current upload destination");
Check(await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, await Verify(), [], default) is null,
    "Intact server backup does not resend unchanged files");
var destination = Path.Combine(backup, "user", "PC1", "Documents");
// Delete only explicitly known fixture files, never live backup data.
File.Delete(Path.Combine(destination, "file-3.txt"));
File.Delete(Path.Combine(destination, "file-8.txt"));
var verified = await Verify();
Check(verified.Count == 8 && state.Count == 10, "Missing server files invalidate history for the run without destroying local state");
var repair = await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, verified, [], default) ?? throw new Exception("Repair skipped");
Check(repair.IncludedFiles.Count == 2, "Only the two deleted copies are packaged despite unchanged local timestamps");
await Upload(repair);
Check(await File.ReadAllTextAsync(Path.Combine(destination, "file-3.txt")) == "original contents 3", "Missing server file contents recovered");
Check(await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, await Verify(), [], default) is null,
    "Following scan completes without another repair upload");
for (var i = 0; i < 10; i++) File.Delete(Path.Combine(destination, $"file-{i}.txt"));
Directory.Delete(destination); // Empty, fixture-only folder.
Check((await Verify()).Count == 0, "Deleted entire user folder is detected");
var all = await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, await Verify(), [], default) ?? throw new Exception("Full repair skipped");
Check(all.IncludedFiles.Count == 10, "Deleting all server files causes all ten unchanged local files to be repackaged");
await Upload(all);
Check((await Verify()).Count == 10, "Full repair is confirmed from actual server files");
await File.WriteAllTextAsync(Path.Combine(destination, "file-0.txt"), "short");
Check((await Verify()).Count == 9, "Truncated server copy is not accepted as confirmed");
Check(Directory.EnumerateFiles(local).Count() == 10, "Repair never deletes local originals");
var batchState = Enumerable.Range(0, 501).ToDictionary(i => "source-" + i, i => new BackupFileStateEntry
    { SourcePath = "source-" + i, ArchiveEntryPath = $"Documents/missing-{i}.txt", Length = 10 });
var before = handler.Requests;
Check((await api.VerifyBackupStateAsync(config, identity, batchState, null, default)).Count == 0 && handler.Requests - before == 2,
    "Large history is verified in bounded batches");
foreach (var path in new[] { "../outside.txt", "Documents/../../escape", "Documents/file:ads", "_Versions/old.txt" })
{
    var rejected = false;
    try { await presence.CheckAsync(new BackupPresenceRequest { DomainUser = identity.DomainUser, ComputerName = identity.ComputerName,
        Files = [new() { RelativePath = path }] }, default); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { rejected = true; }
    Check(rejected, "Invalid check path rejected: " + path);
}
clientRecord.IsDisabled = true;
await clients.SaveAsync([clientRecord]);
var denied = false;
try { await presence.CheckAsync(new BackupPresenceRequest { DomainUser = identity.DomainUser, ComputerName = identity.ComputerName }, default); }
catch (UnauthorizedAccessException) { denied = true; }
Check(denied, "Disabled client cannot check backups");
handler.Failure = HttpStatusCode.ServiceUnavailable;
var failed = false;
try { await Verify(); } catch (HttpRequestException) { failed = true; }
Check(failed && state.Count == 10, "Server failure cannot produce a verified no-changes result or erase history");
handler.Failure = HttpStatusCode.NotFound;
failed = false;
try { await Verify(); } catch (HttpRequestException ex) { failed = ex.Message.Contains("1.0.4"); }
Check(failed, "Old server returns explicit upgrade instruction");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
failed = false;
try { await api.VerifyBackupStateAsync(config, identity, state, null, cancelled.Token); } catch (OperationCanceledException) { failed = true; }
Check(failed, "Cancellation is not mistaken for verified completion");

clientRecord.IsDisabled = false;
await clients.SaveAsync([clientRecord]);
handler.Failure = null;
// Exercise empty nested folders after an already confirmed backup, not just first-run packaging.
var empty = Path.Combine(local, "Parent", "Empty");
Directory.CreateDirectory(empty);
var folders = await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, state, [], default)
    ?? throw new Exception("New empty folder was ignored");
Check(folders.IncludedFiles.Count == 2 && folders.IncludedFiles.All(f => f.IsDirectory),
    "Smart scan includes new empty parent and child folders without resending unchanged files");
Check(JsonSerializer.Deserialize<BackupUploadMetadata>(folders.MetadataJson)!.FileCount == 0,
    "Directory-only package keeps the actual file count at zero");
using (var zip = System.IO.Compression.ZipFile.OpenRead(folders.FilePath))
    Check(zip.GetEntry("Documents/Parent/Empty/") is not null, "ZIP carries explicit trailing-slash directory entry");
await Upload(folders);
Check(Directory.Exists(Path.Combine(destination, "Parent", "Empty")), "Real server creates the empty nested folder");
// Use confirmed state here because file-0 was intentionally truncated in an earlier test.
Check(await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, state, [], default) is null,
    "Confirmed folders do not loop through subsequent backup packages");
Check((await Verify()).Values.Count(f => f.IsDirectory) == 2, "Server verifies actual folder presence");
Directory.Delete(Path.Combine(destination, "Parent", "Empty")); // Known empty fixture only.
verified = await Verify();
Check(!verified.ContainsKey(empty), "Deleted server folder invalidates only that directory record");
var folderRepair = await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, verified, [], default)
    ?? throw new Exception("Missing server directory was ignored");
Check(folderRepair.IncludedFiles.Count(f => f.IsDirectory) == 1, "Missing server empty folder is repackaged");
await Upload(folderRepair);
Check(await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, await Verify(), [], default) is null,
    "Verified repaired files and folders produce no further upload");
await File.WriteAllTextAsync(Path.Combine(empty, "later.txt"), "added after directory confirmation");
var later = await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, await Verify(), [], default)
    ?? throw new Exception("File in previously empty directory was ignored");
Check(later.IncludedFiles.Count == 1 && !later.IncludedFiles[0].IsDirectory,
    "File added inside a confirmed folder is detected without resending its directory");
await Upload(later);
Directory.CreateDirectory(Path.Combine(local, "Cache", "ExcludedEmpty"));
Check(await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, await Verify(), [], default) is null,
    "Excluded empty subfolders remain excluded");
var wrongType = await presence.CheckAsync(new BackupPresenceRequest
{
    DomainUser = identity.DomainUser, ComputerName = identity.ComputerName,
    Files = [new() { RelativePath = "Documents/Parent/Empty", IsDirectory = false },
             new() { RelativePath = "Documents/file-1.txt", IsDirectory = true }]
}, default);
Check(wrongType.PresentPaths.Count == 0, "Files and directories cannot satisfy one another's presence check");
handler.LegacyDirectories = true;
failed = false;
try { await Verify(); } catch (InvalidOperationException ex) { failed = ex.Message.Contains("1.0.11"); }
Check(failed, "Old server receives only capability probe and gives a clear folder-support upgrade message");
handler.LegacyDirectories = false;
var directoryOnlyConfig = new ClientAgentConfig { ServerUrl = "http://fixture", BackupFolders = ["Desktop"] };
var onlyRoot = Path.Combine(fixture, "directory-only");
Directory.CreateDirectory(Path.Combine(onlyRoot, "Empty"));
var onlyBuilder = new BackupPackageBuilder(new FolderMappingService(_ => onlyRoot), new ClientIdentityService(),
    NullLogger<BackupPackageBuilder>.Instance, Path.Combine(fixture, "only-queue"));
var onlyPackage = await onlyBuilder.CreateSmartBackupAsync(directoryOnlyConfig, null,
    new Dictionary<string, BackupFileStateEntry>(), [], default) ?? throw new Exception("Directory-only initial backup ignored");
Check(onlyPackage.IncludedFiles.Count == 1 && onlyPackage.IncludedFiles[0].IsDirectory,
    "Initial backup containing only an empty subfolder has confirmable state");
Check(await onlyBuilder.CreateSmartBackupAsync(directoryOnlyConfig, DateTimeOffset.UtcNow,
    onlyPackage.IncludedFiles.ToDictionary(f => f.SourcePath), [], default) is null,
    "Directory-only initial backup does not fall back to an endless full backup");
for (var i = 0; i < 2001; i++) Directory.CreateDirectory(Path.Combine(onlyRoot, "batch-" + i));
var onlyState = onlyPackage.IncludedFiles.ToDictionary(f => f.SourcePath, StringComparer.OrdinalIgnoreCase);
var firstBatch = await onlyBuilder.CreateSmartBackupAsync(directoryOnlyConfig, DateTimeOffset.UtcNow, onlyState, [], default)
    ?? throw new Exception("Directory batch missing");
Check(firstBatch.IncludedFiles.Count == 2000, "Large directory scans use bounded packages");
foreach (var entry in firstBatch.IncludedFiles) onlyState[entry.SourcePath] = entry;
var secondBatch = await onlyBuilder.CreateSmartBackupAsync(directoryOnlyConfig, DateTimeOffset.UtcNow, onlyState, [], default)
    ?? throw new Exception("Remaining directory missing");
Check(secondBatch.IncludedFiles.Count == 1, "Next package contains the remaining directory without duplicates");
failed = false;
try { await onlyBuilder.CreateSmartBackupAsync(directoryOnlyConfig, DateTimeOffset.UtcNow, onlyState, [], cancelled.Token); }
catch (OperationCanceledException) { failed = true; }
Check(failed, "Cancellation interrupts directory-only scans");
Console.WriteLine($"All {count} backup repair checks passed. Only isolated fixture files were touched.");

sealed class FixtureFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
sealed class PresenceHandler(BackupPresenceService service) : HttpMessageHandler
{
    public int Requests;
    public HttpStatusCode? Failure;
    public bool LegacyDirectories;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests++;
        if (Failure.HasValue) return new HttpResponseMessage(Failure.Value);
        var input = await request.Content!.ReadFromJsonAsync<BackupPresenceRequest>(cancellationToken: cancellationToken);
        if (LegacyDirectories)
        {
            if (input!.Files.Any(f => f.IsDirectory)) throw new Exception("Directory request sent to an old server");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { PresentPaths = Array.Empty<string>() }) };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(await service.CheckAsync(input!, cancellationToken)) };
    }
}
