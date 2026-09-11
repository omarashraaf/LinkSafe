using Shared.Storage;
using System.Xml.Linq;

var root = Path.GetFullPath(Path.Combine("artifacts", "startup-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var exe = Path.Combine(root, "LinkSafe & Agent.exe");
await File.WriteAllTextAsync(exe, "fixture; never executed");
const string sid = "S-1-5-21-1-2-3-1001";
var xml = AgentStartupTask.Definition(exe, sid);
await File.WriteAllTextAsync(Path.Combine(root, "definition.xml"), xml);
int passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS: " + label); }
AgentStartupTask.Validate(xml, exe, sid);
Check(true, "Generated task verifies, including spaces and XML-special characters in paths");
var ns = XNamespace.Get("http://schemas.microsoft.com/windows/2004/02/mit/task");
var document = XDocument.Parse(xml);
Check(document.Descendants(ns + "UserId").All(node => node.Value == sid), "Principal and logon trigger use the same explicit user SID");
Check(document.Descendants(ns + "Delay").Single().Value == "PT15S", "Startup waits briefly for the user profile");
Check(!document.Descendants(ns + "Command").Single().Value.Contains('"'), "Executable is not incorrectly quoted inside XML");
foreach (var change in new[] { ("UserId", "S-1-5-18"), ("DisallowStartIfOnBatteries", "true"), ("StopIfGoingOnBatteries", "true"),
    ("ExecutionTimeLimit", "PT72H"), ("LogonType", "Password"), ("RunLevel", "HighestAvailable"), ("Enabled", "false"),
    ("RunOnlyIfNetworkAvailable", "true"), ("Command", @"C:\Wrong.exe"), ("WorkingDirectory", @"C:\Windows"), ("Count", "0") })
{
    var changed = XDocument.Parse(xml);
    changed.Descendants(ns + change.Item1).First().Value = change.Item2;
    try { AgentStartupTask.Validate(changed.ToString(), exe, sid); throw new Exception("Invalid task accepted"); }
    catch (InvalidDataException) { Check(true, "Invalid startup rejected: " + change.Item1); }
}
var calls = new List<string[]>();
string? created = null;
var scheduler = new AgentStartupTask(async args =>
{
    calls.Add(args);
    if (args[0] == "/Create") { created = await File.ReadAllTextAsync(args[Array.IndexOf(args, "/XML") + 1]); return new(0, "created", ""); }
    return created is null ? new(1, "", "not found") : new(0, created, "");
});
await scheduler.InstallAsync(exe, sid);
Check(calls.All(args => args[Array.IndexOf(args, "/TN") + 1] == AgentStartupTask.NameForUser(sid)), "Every task operation uses the current user's SID-specific name");
Check(calls.Count == 3 && calls.Last()[0] == "/Query", "Enable verifies the persisted task after creation");
Check(!calls.Single(args => args[0] == "/Create").Contains("/F"), "Unverified existing task is never force overwritten");
Check(!File.Exists(calls.Single(args => args[0] == "/Create")[4]), "Temporary task XML is removed after installation");
var failures = new AgentStartupTask(args => Task.FromResult(args[0] == "/Query" ? new SchedulerResult(1, "", "not found") : new(5, "", "Access denied")));
try { await failures.InstallAsync(exe, sid); throw new Exception("Failure hidden"); }
catch (InvalidOperationException ex) { Check(ex.Message.Contains("Access denied"), "Task creation errors propagate instead of reporting enabled"); }
var unrelated = new AgentStartupTask(args => Task.FromResult(new SchedulerResult(0, xml.Replace(sid, "S-1-5-18"), "")));
try { await unrelated.InstallAsync(exe, sid); throw new Exception("Other user replaced"); }
catch (InvalidOperationException) { Check(true, "Another user's task is not replaced"); }
try { await unrelated.RemoveAsync(sid); throw new Exception("Other user removed"); }
catch (InvalidOperationException) { Check(true, "Another user's task is not deleted"); }
var invalidReadback = new AgentStartupTask(args => Task.FromResult(args[0] == "/Query" ? new SchedulerResult(0, xml.Replace("<ExecutionTimeLimit>PT0S", "<ExecutionTimeLimit>PT72H"), "") : new(0, "", "")));
try { await invalidReadback.InstallAsync(exe, sid); throw new Exception("Readback ignored"); }
catch (InvalidDataException) { Check(true, "Successful create exit code alone cannot pass verification"); }

string? ResolveFixtureUser(string name) => name.Equals("DOMAIN\\user", StringComparison.OrdinalIgnoreCase) ? sid :
    name.Equals("OTHERDOMAIN\\user", StringComparison.OrdinalIgnoreCase) ? "S-1-5-21-4-5-6-1001" : null;
var exported = XDocument.Parse(xml);
exported.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value = "DOMAIN\\user";
exported.Descendants(ns + "RunLevel").Remove();
exported.Descendants(ns + "Enabled").Remove();
exported.Descendants(ns + "RunOnlyIfIdle").Remove();
exported.Descendants(ns + "RunOnlyIfNetworkAvailable").Remove();
AgentStartupTask.Validate(exported.ToString(), exe, sid, ResolveFixtureUser);
Check(true, "Windows-exported account trigger and omitted defaults pass verification");
exported.Descendants(ns + "Principal").Single().Element(ns + "UserId")!.Value = "domain\\USER";
AgentStartupTask.Validate(exported.ToString(), exe, sid, ResolveFixtureUser);
Check(true, "Named principal is compared by resolved SID rather than string spelling");
Check(!AgentStartupTask.SameUser("OTHERDOMAIN\\user", sid, ResolveFixtureUser), "Matching short usernames in different domains are not treated as the same user");
Check(!AgentStartupTask.SameUser("UNKNOWN\\user", sid, ResolveFixtureUser), "Unresolved account fails closed");
Check(!AgentStartupTask.SameUser(null, sid, ResolveFixtureUser), "Missing login identity is not accepted as this user");
var normalizedScheduler = new AgentStartupTask(command => Task.FromResult(new SchedulerResult(0,
    command[0] == "/Query" ? exported.ToString() : "", "")), ResolveFixtureUser);
await normalizedScheduler.InstallAsync(exe, sid);
Check(true, "Repair accepts and verifies a Windows-normalized task owned by the same account");
exported.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Remove();
try { AgentStartupTask.Validate(exported.ToString(), exe, sid, ResolveFixtureUser); throw new Exception("Global logon trigger accepted"); }
catch (InvalidDataException) { Check(true, "An all-users logon trigger still fails verification"); }

if (args.Contains("--verify-installed"))
{
    using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
    await new AgentStartupTask().VerifyAsync(Path.Combine(Path.GetDirectoryName(Shared.Constants.LinkSafeDefaults.ClientConfigPath)!, "bin", "LinkSafeClientAgent.exe"), current.User!.Value);
    Check(true, "Read-only verification of the actual Windows-registered LinkSafe task passes");
}
Console.WriteLine($"{passed} startup checks passed; no real scheduled task or agent was modified.");
Console.WriteLine("Fixture: " + root);
