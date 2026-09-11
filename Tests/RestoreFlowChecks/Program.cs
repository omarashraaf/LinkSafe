using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using LinkSafeServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Models;
using Shared.Storage;

var fixture = Path.GetFullPath(Path.Combine("artifacts", "restore-flow-tests", Guid.NewGuid().ToString("N")));
var backup = Path.Combine(fixture, "server");
var local = Path.Combine(fixture, "client");
Directory.CreateDirectory(backup);
Directory.CreateDirectory(local);
var count = 0;
void Check(bool success, string label) { if (!success) throw new Exception(label); count++; Console.WriteLine("PASS: " + label); }
async Task Write(string root, string relative, string data)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, data);
}
await Write(backup, "Documents/existing.txt", "server version must NOT overwrite local");
await Write(backup, "Documents/missing.txt", "restored document");
await Write(backup, "Documents/fail.txt", "complete server bytes");
await Write(backup, "Desktop/race.txt", "remote race version");
await Write(backup, "Downloads/empty.txt", "");
await Write(backup, "Drives/D/Projects/project.txt", "drive data");
await Write(backup, "_Versions/old-secret.txt", "not current data");
await Write(local, "Documents/existing.txt", "LOCAL KEEP");
await Write(local, "Documents/local-only.txt", "local-only KEEP");
var configService = new ServerConfigService(NullLogger<ServerConfigService>.Instance);
var sanitizer = new PathSanitizer();
var registry = new ClientRegistryService(configService, new StorageSecurityService(NullLogger<StorageSecurityService>.Instance), sanitizer, NullLogger<ClientRegistryService>.Instance);
var registryStore = new JsonFileStore<List<RegisteredClientRecord>>(Path.Combine(fixture, "clients.json"));
typeof(ClientRegistryService).GetField("_clientsStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(registry, registryStore);
await registryStore.SaveAsync([new RegisteredClientRecord { DomainUser = "TEST\\user", Username = "user", ComputerName = "PC1", BackupDestinationPath = backup }]);
var service = new RoamingRestoreService(registry, sanitizer);
var manifest = await service.GetManifestAsync("TEST\\user", "PC1", default) ?? throw new Exception("Manifest missing");
Check(manifest.Files.Count == 6, "Manifest includes user folders and drive data, not versions");
Check((await service.GetOptionsAsync("TEST\\user", "PC1")).Candidates.Count == 0, "Logon discovery excludes the current PC");
Check((await service.GetOptionsAsync("TEST\\user", "PC1", includeCurrentComputer: true)).Candidates.Count == 1, "Manual discovery includes this PC's backup");
Check(await service.GetManifestAsync("TEST\\other-user", "PC1", default) is null, "Registry lookup stays within the requested user's source");
var roots = new Dictionary<string, string> { ["Documents"] = Path.Combine(local, "Documents"), ["Desktop"] = Path.Combine(local, "Desktop"), ["Downloads"] = Path.Combine(local, "Downloads"), ["Drives/D/Projects"] = Path.Combine(local, "Projects") };
var handler = new FixtureHandler(service, local);
using var client = new HttpClient(handler) { BaseAddress = new Uri("http://fixture/"), Timeout = Timeout.InfiniteTimeSpan };
var plan = await MissingFileRestore.CompareAsync(client, "TEST\\user", "PC1", roots);
Check(plan.Existing == 1 && plan.Missing.Count == 5, "Comparison identifies exactly the missing files");
Check(handler.FileRequests.Count == 0, "Comparison downloads metadata only, not file contents");
var progress = new List<int>();
var result = await MissingFileRestore.ApplyAsync(client, plan, (message, percent) => { progress.Add(percent); return Task.CompletedTask; });
Check(result.Restored == 3 && result.Preserved == 2 && result.Errors.Count == 1, "Partial failure and concurrent local creation reported accurately");
Check(await File.ReadAllTextAsync(Path.Combine(local, "Documents/existing.txt")) == "LOCAL KEEP", "Existing local file content is unchanged");
Check(await File.ReadAllTextAsync(Path.Combine(local, "Documents/local-only.txt")) == "local-only KEEP", "Local-only file is not deleted");
Check(await File.ReadAllTextAsync(Path.Combine(local, "Documents/missing.txt")) == "restored document", "Missing document restored in place");
Check(await File.ReadAllTextAsync(Path.Combine(local, "Desktop/race.txt")) == "LOCAL RACE KEEP", "File created during download is not overwritten");
Check(File.Exists(Path.Combine(local, "Downloads/empty.txt")) && new FileInfo(Path.Combine(local, "Downloads/empty.txt")).Length == 0, "Zero-byte missing file restored");
Check(await File.ReadAllTextAsync(Path.Combine(local, "Projects/project.txt")) == "drive data", "Enabled additional source restores to its mapped folder");
Check(!File.Exists(Path.Combine(local, "Documents/fail.txt")), "Truncated response never creates a partial destination");
Check(!Directory.EnumerateFiles(local, ".linksafe-restore-*", SearchOption.AllDirectories).Any(), "Temporary downloads are removed after failures");
Check(!handler.FileRequests.Contains("Documents/existing.txt"), "Existing file contents are never requested");
Check(progress.Count > 0 && progress.Last() == 100 && progress.All(p => p is >= 0 and <= 100), "Restore progress completes without looping");
handler.FailDownload = false;
var retry = await MissingFileRestore.CompareAsync(client, "TEST\\user", "PC1", roots);
Check(retry.Missing.Count == 1 && retry.Missing[0].File.RelativePath == "Documents/fail.txt", "Retry compares again and selects only the failed missing file");
Check((await MissingFileRestore.ApplyAsync(client, retry)).Restored == 1, "Retry restores the remaining file");
var noChanges = await MissingFileRestore.CompareAsync(client, "TEST\\user", "PC1", roots);
var requestsBefore = handler.FileRequests.Count;
await MissingFileRestore.ApplyAsync(client, noChanges);
Check(noChanges.Missing.Count == 0 && handler.FileRequests.Count == requestsBefore, "Repeat restore does not download existing files");
foreach (var unsafePath in new[] { "../outside.txt", "Documents/../../outside.txt", "Documents/../DocumentsOther/file", "C:/Windows/file", "Documents/file:ads", "Documents/CON.txt" })
{
    var rejected = false;
    try { await service.OpenRestoreFileAsync("TEST\\user", "PC1", unsafePath, 0, 0, default); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Unsafe server file request rejected: " + unsafePath);
}
var blocked = MissingFileRestore.Compare(manifest, "TEST\\user", "PC1", new Dictionary<string, string>(), []);
Check(blocked.Missing.Count == 0 && blocked.Warnings.Count > 0, "Unmapped drive/folder data is reported rather than silently restored elsewhere");
var oldFile = manifest.Files.Single(f => f.RelativePath == "Documents/missing.txt");
await Write(backup, "Documents/missing.txt", "changed server data after comparison");
var changedRejected = false;
try { await service.OpenRestoreFileAsync("TEST\\user", "PC1", oldFile.RelativePath, oldFile.Length, oldFile.LastWriteTimeUtc.Ticks, default); }
catch (InvalidOperationException) { changedRejected = true; }
Check(changedRejected, "File changed since comparison requires a fresh check");
await Write(backup, "Documents/cancel.txt", "cancelled download must not appear locally");
using (var cancellation = new CancellationTokenSource())
{
    handler.CancelDownload = cancellation;
    var cancelPlan = await MissingFileRestore.CompareAsync(client, "TEST\\user", "PC1", roots);
    var cancelled = false;
    try { await MissingFileRestore.ApplyAsync(client, cancelPlan, cancellationToken: cancellation.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && !File.Exists(Path.Combine(local, "Documents/cancel.txt")), "Cancellation during streaming does not create a destination file");
    Check(!Directory.EnumerateFiles(local, ".linksafe-restore-*", SearchOption.AllDirectories).Any(), "Cancellation cleans up the partial temporary download");
}
try
{
    var outside = Path.Combine(fixture, "outside");
    Directory.CreateDirectory(outside);
    Directory.CreateSymbolicLink(Path.Combine(local, "Documents/link"), outside);
    var unsafeManifest = new RestoreManifest { Files = [new RestoreFileEntry { RelativePath = "Documents/link/escape.txt" }] };
    var linked = MissingFileRestore.Compare(unsafeManifest, "TEST\\user", "PC1", roots);
    Check(linked.Missing.Count == 0 && linked.Warnings.Count == 1, "Local links cannot redirect restores outside the profile");
}
catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: symbolic-link fixture requires OS permission; no permission changed."); }
catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314) { Console.WriteLine("SKIP: symbolic-link fixture requires OS permission; no permission changed."); }
Console.WriteLine($"All {count} restore checks passed. Only isolated fixture files were read/written; no live restore was run.");

sealed class FixtureHandler(RoamingRestoreService service, string local) : HttpMessageHandler
{
    public bool FailDownload { get; set; } = true;
    public CancellationTokenSource? CancelDownload { get; set; }
    public List<string> FileRequests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        if (request.RequestUri.AbsolutePath.EndsWith("/manifest"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(await service.GetManifestAsync(query["domainUser"], query["sourceComputerName"], cancellationToken)) };
        var relative = query["relativePath"];
        FileRequests.Add(relative);
        if (relative == "Documents/cancel.txt" && CancelDownload is not null)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelMidstream(CancelDownload)) };
        if (relative == "Desktop/race.txt")
        {
            Directory.CreateDirectory(Path.Combine(local, "Desktop"));
            await File.WriteAllTextAsync(Path.Combine(local, "Desktop/race.txt"), "LOCAL RACE KEEP", cancellationToken);
        }
        if (relative == "Documents/fail.txt" && FailDownload)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableShortStream()) };
        var stream = await service.OpenRestoreFileAsync(query["domainUser"], query["sourceComputerName"], relative, long.Parse(query["length"]), long.Parse(query["lastWriteTicks"]), cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream!) };
    }
}
sealed class NonSeekableShortStream : MemoryStream
{
    public NonSeekableShortStream() : base([1, 2]) { }
    public override bool CanSeek => false;
}
sealed class CancelMidstream(CancellationTokenSource cancellation) : MemoryStream(new byte[] { 1, 2 })
{
    private bool read;
    public override bool CanSeek => false;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (read) { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
        read = true;
        return base.ReadAsync(buffer, cancellationToken);
    }
}
