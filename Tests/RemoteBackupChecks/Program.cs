using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using LinkSafeClientAgent.Services;
using LinkSafeServer.Controllers;
using LinkSafeServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Models;
using Shared.Storage;

var fixture = Path.GetFullPath(Path.Combine("artifacts", "remote-backup-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixture);
var count = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS: " + label); }
void Inject(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
var configs = new ServerConfigService(NullLogger<ServerConfigService>.Instance);
Inject(configs, "_store", new JsonFileStore<ServerConfig>(Path.Combine(fixture, "config.json")));
var registry = new ClientRegistryService(configs, new StorageSecurityService(NullLogger<StorageSecurityService>.Instance), new PathSanitizer(), NullLogger<ClientRegistryService>.Instance);
var clients = new JsonFileStore<List<RegisteredClientRecord>>(Path.Combine(fixture, "clients.json"));
Inject(registry, "_clientsStore", clients);
var records = new List<RegisteredClientRecord> {
    new() { DomainUser = "TEST\\user", Username = "user", ComputerName = "ONLINE-PC", AgentVersion = "1.0.16", LastSeenAt = DateTimeOffset.UtcNow, UsesLocalFolderSelection = true, ReportedBackupFolders = ["Desktop"] },
    new() { DomainUser = "TEST\\user", Username = "user", ComputerName = "OFFLINE-PC", AgentVersion = "1.0.16", LastSeenAt = DateTimeOffset.UtcNow.AddDays(-1) },
    new() { DomainUser = "TEST\\user", Username = "user", ComputerName = "OLD-PC", AgentVersion = "1.0.15" },
    new() { DomainUser = "TEST\\user", Username = "user", ComputerName = "DISABLED-PC", AgentVersion = "1.0.16", IsDisabled = true }
};
await clients.SaveAsync(records);
var requestsFile = Path.Combine(fixture, "requests.json");
var service = new RemoteBackupService(registry, requestsFile);
var jobs = new BackupJobService();
Inject(jobs, "_store", new JsonFileStore<List<BackupJobRecord>>(Path.Combine(fixture, "jobs.json")));
var logs = new LogReaderService(Path.Combine(fixture, "server.log"));
var dashboard = new DashboardService(configs, registry, jobs);
await configs.SaveAsync(new ServerConfig { BackupRoot = Path.Combine(fixture, "backup-files") });
Directory.CreateDirectory(Path.Combine(fixture, "backup-files"));
var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.EnqueueAsync("TEST\\user", "ONLINE-PC", default)));
Check(results.Select(x => x.RequestId).Distinct().Count() == 1, "Concurrent duplicate clicks produce one request");
var request = results[0];
Check(request.Status == "Queued" && !request.IsTerminal, "Enqueue is not success");
service = new RemoteBackupService(registry, requestsFile);
Check((await service.GetAllAsync(default)).Single().RequestId == request.RequestId, "Server restart preserves queued request");
foreach (var computer in new[] { "OLD-PC", "DISABLED-PC", "MISSING-PC" })
{
    var rejected = false;
    try { await service.EnqueueAsync("TEST\\user", computer, default); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Unsupported, disabled or unknown client is rejected: " + computer);
}
await service.PollAsync(new() { DomainUser = "TEST\\user", ComputerName = "ONLINE-PC", RequestId = Guid.NewGuid().ToString(), Status = "Success" }, default);
Check((await service.GetAllAsync(default)).Single().Status == "Queued", "Unrelated completion cannot complete request");
var inboxFile = Path.Combine(fixture, "inbox.json");
var inbox = new RemoteBackupInbox(inboxFile);
await inbox.AcceptAsync("http://fixture", request, default);
var executions = 0;
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var run = inbox.ExecuteAsync("http://fixture", async (id, ct) => { executions++; started.SetResult(); await release.Task.WaitAsync(ct); return ("Success", "All selected folders scanned."); }, default);
await started.Task;
await inbox.AcceptAsync("http://fixture", request, default).WaitAsync(TimeSpan.FromSeconds(2));
Check((await inbox.ReadAsync("http://fixture", default))?.Status == "Running", "Duplicate delivery cannot reset running work or block polling");
release.SetResult(); await run;
var completed = (await inbox.ReadAsync("http://fixture", default))!;
await service.PollAsync(new() { DomainUser = request.DomainUser, ComputerName = request.ComputerName, RequestId = request.RequestId, Status = completed.Status, Message = completed.Message }, default);
Check((await service.GetAllAsync(default)).Single().Status == "Success", "Matching agent completion reaches server");
inbox = new RemoteBackupInbox(inboxFile);
await inbox.AcceptAsync("http://fixture", request, default);
await inbox.ExecuteAsync("http://fixture", (id, ct) => { executions++; return Task.FromResult(("Success", "duplicate")); }, default);
Check(executions == 1, "Restart and lost completion response do not repeat finished request");
Check(await inbox.ReadAsync("http://different-server", default) is null, "Inbox never reports old-server state to another server");
var next = await service.EnqueueAsync("TEST\\user", "ONLINE-PC", default);
Check(next.RequestId != request.RequestId, "New click after completion creates a new request");
await inbox.AcceptAsync("http://fixture", next, default);
await inbox.ExecuteAsync("http://fixture", (_, _) => throw new IOException("Fixture failure"), default);
Check((await inbox.ReadAsync("http://fixture", default))?.Status == "Failed", "Execution failure is durable, not success");
var offline = await service.EnqueueAsync("TEST\\user", "OFFLINE-PC", default);
Check(offline.Status == "Queued", "Offline client can be queued without claiming execution");
await service.PollAsync(new() { DomainUser = offline.DomainUser, ComputerName = offline.ComputerName, RequestId = offline.RequestId, Status = "Blocked", Message = "Backups paused locally." }, default);
Check((await service.GetAllAsync(default)).Single(x => x.ComputerName == "OFFLINE-PC").Status == "Blocked", "Local pause is visible without overriding it");
var interrupted = new RemoteBackupInbox(Path.Combine(fixture, "interrupted.json"));
await interrupted.AcceptAsync("http://fixture", new RemoteBackupRequest { RequestId = Guid.NewGuid().ToString() }, default);
using (var cancel = new CancellationTokenSource())
{
    try { await interrupted.ExecuteAsync("http://fixture", (_, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(("Success", "wrong")); }, cancel.Token); }
    catch (OperationCanceledException) { }
}
Check((await interrupted.ReadAsync("http://fixture", default))?.Status == "Stopped", "Interrupted server request persists Stopped rather than success");

// Isolated loopback web host uses the real Razor page and API, never live ProgramData or backups.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(RemoteBackupController).Assembly.FullName,
    WebRootPath = Path.GetFullPath("LinkSafe/LinkSafeServer/wwwroot") });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(fixture, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(o => o.Conventions.AuthorizeFolder("/"));
builder.Services.AddControllers();
builder.Services.AddSingleton(registry); builder.Services.AddSingleton(service); builder.Services.AddSingleton(configs);
builder.Services.AddSingleton(jobs); builder.Services.AddSingleton(logs); builder.Services.AddSingleton(dashboard);
var app = builder.Build();
app.UseStaticFiles(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
app.MapRazorPages(); app.MapControllers();
app.MapGet("/fixture-login", async (HttpContext context) => {
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Fixture admin")], CookieAuthenticationDefaults.AuthenticationScheme)));
    context.Response.Redirect("/Clients");
});
await app.StartAsync();
var url = app.Urls.Single();
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(url) };
try
{
    var unauthorized = await http.PostAsync("/Clients?handler=Backup", new FormUrlEncodedContent(new Dictionary<string,string> { ["domainUser"]="TEST\\user", ["computerName"]="ONLINE-PC" }));
    Check(unauthorized.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest, "Unauthenticated POST cannot create a backup request");
    await http.GetAsync("/fixture-login");
    var html = await http.GetStringAsync("/Clients");
    Check(html.Contains("Back up now") && WebUtility.HtmlDecode(html).Contains("Requires agent 1.0.16+") && html.Contains("Blocked"), "Actual Razor page renders action, compatibility and pending status");
    var noCsrf = await http.PostAsync("/Clients?handler=Backup", new FormUrlEncodedContent(new Dictionary<string,string> { ["domainUser"]="TEST\\user", ["computerName"]="ONLINE-PC" }));
    Check(noCsrf.StatusCode == HttpStatusCode.BadRequest, "Authenticated POST without antiforgery token is rejected");
    var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    Check(!string.IsNullOrEmpty(token), "Backup form includes antiforgery token");
    var posted = await http.PostAsync("/Clients?handler=Backup", new FormUrlEncodedContent(new Dictionary<string,string> {
        ["domainUser"]="TEST\\user", ["computerName"]="ONLINE-PC", ["__RequestVerificationToken"]=token }));
    Check(posted.StatusCode == HttpStatusCode.Redirect, "Admin POST queues backup through the actual page handler");
    using var apiResponse = await http.PostAsJsonAsync("/api/client/backup-request/poll", new RemoteBackupPoll { DomainUser="TEST\\user", ComputerName="ONLINE-PC" });
    Check(apiResponse.IsSuccessStatusCode && (await apiResponse.Content.ReadAsStringAsync()).Contains(next.RequestId), "Actual agent endpoint returns only the matching pending request");
    var agentApi = new ClientApiService(new PollHttpFactory());
    var delivered = await agentApi.PollBackupRequestAsync(new ClientAgentConfig { ServerUrl = url }, new RemoteBackupPoll { DomainUser="TEST\\user", ComputerName="ONLINE-PC" }, default);
    Check(delivered?.RequestId == next.RequestId, "Production agent HTTP client deserializes the server dispatch");
    using var missing = await http.PostAsJsonAsync("/api/client/backup-request/poll", new RemoteBackupPoll { DomainUser="OTHER\\user", ComputerName="ONLINE-PC" });
    Check(missing.StatusCode == HttpStatusCode.Conflict, "Unknown identity cannot poll a registered client's request");
    count += await BulkChecks.RunAsync(http, registry, service, fixture);
    count += await LiveChecks.RunAsync(http, configs, registry, dashboard, logs, fixture);
    Console.WriteLine($"All {count} remote backup checks passed. Fixture: {fixture}");
    if (args.Contains("--serve")) { Console.WriteLine("FIXTURE_URL=" + url + "/fixture-login"); await Task.Delay(TimeSpan.FromMinutes(15)); }
}
finally { await app.StopAsync(); await app.DisposeAsync(); }

sealed class PollHttpFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
