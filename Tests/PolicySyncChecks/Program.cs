using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using LinkSafeClientAgent.Services;
using LinkSafeServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Models;
using Shared.Storage;

var root = Path.GetFullPath(Path.Combine("artifacts", "policy-sync-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS: " + name); }
void Inject(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
var raw = new JsonFileStore<ClientAgentConfig>(Path.Combine(root, "client-agent-config.json"));
await raw.SaveAsync(new() { ServerUrl = "http://fixture", BackupFolders = ["Documents"], IsAgentEnabled = true });
var service = new AgentConfigService(root);
var policies = service.Policies;
var serverPolicy = new ClientPolicyResponse { BackupFolders = ["Desktop", "Documents"], CustomFolders = [@"D:\Projects"], DelayBeforeCopySeconds = 7, MaxFileSizeMB = 90, ProtectedSourcePaths = [@"D:\Backups"] };
await policies.SavePolicyAsync("http://fixture", serverPolicy);
var config = await service.GetAsync();
Check(config.BackupFolders.SequenceEqual(serverPolicy.BackupFolders), "Server folders replace stale cached client configuration");
Check(config.DelayBeforeCopySeconds == 7 && config.MaxFileSizeMB == 90, "Server timing and limits propagate");
Check((await policies.GetPolicyAsync()).SyncedAt is not null, "Policy synchronization has a visible timestamp");
await policies.SaveSelectionAsync(new() { ServerUrl = "http://fixture", OverrideServerFolders = true, BackupFolders = ["Pictures"], CustomFolders = [@"E:\Personal"] });
config = await service.GetAsync();
Check(config.BackupFolders.SequenceEqual(["Pictures"]) && config.CustomFolders.SequenceEqual([@"E:\Personal"]), "Local selection replaces rather than adds to server selection");
serverPolicy.BackupFolders = ["Downloads"];
serverPolicy.MaxFileSizeMB = 75;
await policies.SavePolicyAsync("http://fixture", serverPolicy);
config = await service.GetAsync();
Check(config.BackupFolders.SequenceEqual(["Pictures"]) && config.MaxFileSizeMB == 75, "Server refresh retains local override but updates server limits");
Check(config.ProtectedSourcePaths.SequenceEqual([@"D:\Backups"]), "Local override cannot remove server source protection");
config.Registered = true;
await service.SaveAsync(config);
Check((await raw.LoadAsync(() => new())).BackupFolders.SequenceEqual(["Documents"]), "Registration never persists effective local folders into base configuration");
await policies.SaveSelectionAsync(new() { ServerUrl = "http://fixture", OverrideServerFolders = false });
Check((await service.GetAsync()).BackupFolders.SequenceEqual(["Downloads"]), "Use server settings restores latest server selection immediately");
var other = new ClientAgentConfig { ServerUrl = "http://other", BackupFolders = ["Music"] };
await policies.ApplyAsync(other);
Check(other.BackupFolders.SequenceEqual(["Music"]), "Cached policy is scoped to its server");
await policies.SaveSelectionAsync(new() { ServerUrl = "http://fixture", OverrideServerFolders = true });
Check((await service.GetAsync()).BackupFolders.Count == 0, "An explicit empty local selection does not fall back to server folders");
try { await policies.SaveSelectionAsync(new() { CustomFolders = [@"D:\..\Windows"] }); throw new Exception("Accepted traversal"); }
catch (ArgumentException) { Check(true, "Unsafe custom folder is rejected"); }
await policies.SaveSelectionAsync(new() { ServerUrl = "http://fixture", OverrideServerFolders = false });
config = await service.GetAsync();
var key = ClientPolicyStore.ScanKey(config);
config.BackupFolders.Add("Documents");
Check(ClientPolicyStore.ScanKey(config) != key, "Source changes invalidate the scan key");
key = ClientPolicyStore.ScanKey(config);
config.BackupFolders.Reverse();
Check(ClientPolicyStore.ScanKey(config) == key, "Reordering selection does not trigger redundant scans");

// Hold a simulated upload open while the production inventory/policy worker refreshes.
var handler = new FixtureHandler { Policy = serverPolicy };
var api = new ClientApiService(new FixtureFactory(handler));
await File.WriteAllTextAsync(Path.Combine(root, "package.zip"), "fixture");
using var uploadToken = new CancellationTokenSource();
var upload = api.UploadAsync(config, Path.Combine(root, "package.zip"), "{}", uploadToken.Token);
await handler.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
using var worker = new DriveInventoryWorker(service, new ClientIdentityService(), api, NullLogger<DriveInventoryWorker>.Instance);
handler.Policy = new ClientPolicyResponse { BackupFolders = ["Desktop", "Documents"], CustomFolders = [] };
await worker.StartAsync(default);
using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(8));
while (!(await service.GetAsync()).BackupFolders.Contains("Desktop")) await Task.Delay(20, wait.Token);
Check(!upload.IsCompleted, "Production worker refreshes server policy while an upload is still busy");
await worker.StopAsync(default);
uploadToken.Cancel();
try { await upload; } catch (OperationCanceledException) { }

var local = Path.Combine(root, "profile");
Directory.CreateDirectory(Path.Combine(local, "Documents"));
Directory.CreateDirectory(Path.Combine(local, "Desktop"));
await File.WriteAllTextAsync(Path.Combine(local, "Documents", "old.txt"), "old");
await File.WriteAllTextAsync(Path.Combine(local, "Desktop", "already-existing.txt"), "capture without watcher event");
var builder = new BackupPackageBuilder(new FolderMappingService(name => Path.Combine(local, name)), new ClientIdentityService(),
    NullLogger<BackupPackageBuilder>.Instance, Path.Combine(root, "queue"));
var oldConfig = new ClientAgentConfig { BackupFolders = ["Documents"] };
var initial = await builder.CreateFullBackupAsync(oldConfig, default) ?? throw new Exception("No fixture package");
var state = initial.IncludedFiles.ToDictionary(item => item.SourcePath);
var refreshed = await service.GetAsync();
var added = await builder.CreateSmartBackupAsync(refreshed, DateTimeOffset.UtcNow, state, [], default);
Check(added?.IncludedFiles.Any(file => file.ArchiveEntryPath == "Desktop/already-existing.txt") == true,
    "Scan after policy change captures existing files in newly selected folder without watcher events");

var serverConfig = new ServerConfigService(NullLogger<ServerConfigService>.Instance);
var registry = new ClientRegistryService(serverConfig, new StorageSecurityService(NullLogger<StorageSecurityService>.Instance), new PathSanitizer(), NullLogger<ClientRegistryService>.Instance);
var clients = new JsonFileStore<List<RegisteredClientRecord>>(Path.Combine(root, "clients.json"));
Inject(registry, "_clientsStore", clients);
await clients.SaveAsync([new() { DomainUser = "DOMAIN\\user", ComputerName = "PC" }]);
var tasks = new List<Task>();
for (int i = 0; i < 30; i++)
{
    tasks.Add(registry.UpdateHeartbeatAsync(new() { DomainUser = "DOMAIN\\user", ComputerName = "PC", LastBackupStatus = "Success" }));
    tasks.Add(registry.SaveClientPolicyAsync("DOMAIN\\user", "PC", ["Pictures"], []));
}
await Task.WhenAll(tasks);
var clientRecord = (await registry.GetAsync("DOMAIN\\user", "PC"))!;
Check(clientRecord.HasBackupPolicyOverride && clientRecord.BackupFoldersOverride.SequenceEqual(["Pictures"]) && clientRecord.LastBackupStatus == "Success", "Concurrent heartbeats cannot overwrite saved client policy");
await registry.SaveClientPolicyAsync("DOMAIN\\user", "PC", [], [], default, useServerDefaults: true);
clientRecord = (await registry.GetAsync("DOMAIN\\user", "PC"))!;
Check(!clientRecord.HasBackupPolicyOverride && clientRecord.BackupFoldersOverride.Count == 0, "Reset to server defaults clears legacy override lists and flag");
Check(serverConfig.ToClientPolicy(new ServerConfig { BackupFolders = ["Music"] }, clientRecord).BackupFolders.SequenceEqual(["Music"]), "Reset client follows updated global defaults");
Console.WriteLine($"{passed} policy synchronization checks passed. Fixture: {root}");

sealed class FixtureFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, false);
}
sealed class FixtureHandler : HttpMessageHandler
{
    public ClientPolicyResponse Policy { get; set; } = new();
    public TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.AbsolutePath == "/api/backup/upload")
        {
            UploadStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri.AbsolutePath == "/api/client/register"
            ? JsonContent.Create(new RegistrationResponse { Success = true, Policy = Policy }) : JsonContent.Create(Policy) };
    }
}
