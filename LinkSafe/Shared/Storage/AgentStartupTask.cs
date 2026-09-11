using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;

namespace Shared.Storage;

public sealed record SchedulerResult(int ExitCode, string Output, string Error);

public sealed class AgentStartupTask(Func<string[], Task<SchedulerResult>>? execute = null, Func<string, string?>? resolveAccount = null)
{
    public const string Name = "LinkSafe Client Agent";
    public static string NameForUser(string sid)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sid, @"^S-1-\d+(?:-\d+)+$"))
            throw new ArgumentException("A Windows user SID is required.");
        return Name + " (" + sid + ")";
    }
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly Func<string[], Task<SchedulerResult>> _execute = execute ?? ExecuteAsync;

    public static string Definition(string executable, string sid)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"')) throw new ArgumentException("Agent executable must be an absolute path without quotes.");
        if (!sid.StartsWith("S-1-", StringComparison.Ordinal)) throw new ArgumentException("Windows user SID is required.");
        XElement E(string name, object value) => new(Ns + name, value);
        return new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "LinkSafe background backup for this Windows user at login.")),
            E("Triggers", new XElement(Ns + "LogonTrigger", E("Enabled", "true"), E("UserId", sid), E("Delay", "PT15S"))),
            E("Principals", new XElement(Ns + "Principal", new XAttribute("id", "AgentUser"), E("UserId", sid),
                E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("RunOnlyIfNetworkAvailable", "false"),
                E("AllowStartOnDemand", "true"), E("Enabled", "true"), E("Hidden", "true"), E("RunOnlyIfIdle", "false"),
                E("ExecutionTimeLimit", "PT0S"), new XElement(Ns + "RestartOnFailure", E("Interval", "PT1M"), E("Count", "3"))),
            new XElement(Ns + "Actions", new XAttribute("Context", "AgentUser"), new XElement(Ns + "Exec",
                E("Command", executable), E("WorkingDirectory", Path.GetDirectoryName(executable)!)))).ToString();
    }

    public static bool SameUser(string? identity, string sid, Func<string, string?>? resolveAccount = null)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        if (identity.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            return identity.Equals(sid, StringComparison.OrdinalIgnoreCase);
        return string.Equals((resolveAccount ?? ResolveAccountSid)(identity), sid, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveAccountSid(string account)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            // The logged-in identity is authoritative even when the domain is offline.
            if (account.Equals(current.Name, StringComparison.OrdinalIgnoreCase)) return current.User?.Value;
            return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static void Validate(string xml, string executable, string sid, Func<string, string?>? resolveAccount = null)
    {
        var task = XDocument.Parse(xml).Root ?? throw new InvalidDataException("Missing startup task.");
        var principal = task.Element(Ns + "Principals")?.Element(Ns + "Principal");
        var trigger = task.Element(Ns + "Triggers")?.Element(Ns + "LogonTrigger");
        var settings = task.Element(Ns + "Settings");
        string? Value(XElement? node, string name) => node?.Element(Ns + name)?.Value;
        void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        Require(SameUser(Value(principal, "UserId"), sid, resolveAccount) && SameUser(Value(trigger, "UserId"), sid, resolveAccount), "Startup is not bound to this Windows user's login, or its account could not be resolved.");
        // Task Scheduler omits default-valued elements when exporting registered tasks.
        Require(Value(principal, "LogonType") == "InteractiveToken" && (Value(principal, "RunLevel") ?? "LeastPrivilege") == "LeastPrivilege", "Startup must run in the user's normal interactive session.");
        Require(Value(trigger, "Enabled") != "false" && Value(settings, "Enabled") != "false", "Startup task is disabled.");
        Require(Value(settings, "DisallowStartIfOnBatteries") == "false" && Value(settings, "StopIfGoingOnBatteries") == "false", "Startup is blocked or stopped on battery power.");
        Require(Value(settings, "ExecutionTimeLimit") == "PT0S", "Startup task has a runtime cutoff.");
        Require(Value(settings, "RunOnlyIfNetworkAvailable") != "true" && Value(settings, "RunOnlyIfIdle") != "true", "Startup waits for network or idle conditions.");
        Require(Value(settings, "StartWhenAvailable") == "true" && Value(settings, "MultipleInstancesPolicy") == "IgnoreNew", "Startup recovery/duplicate settings need repair.");
        Require(Value(settings?.Element(Ns + "RestartOnFailure"), "Interval") == "PT1M" && Value(settings?.Element(Ns + "RestartOnFailure"), "Count") == "3", "Startup restart-on-failure settings need repair.");
        var actions = task.Element(Ns + "Actions");
        var action = actions?.Element(Ns + "Exec");
        Require(actions?.Elements().Count() == 1 && string.Equals(Value(action, "Command"), executable, StringComparison.OrdinalIgnoreCase), "Startup points to a different or incorrectly quoted executable.");
        Require(string.IsNullOrWhiteSpace(Value(action, "Arguments")), "Startup has unexpected arguments.");
        Require(string.Equals(Value(action, "WorkingDirectory"), Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase), "Startup working directory is incorrect.");
    }

    public async Task VerifyAsync(string executable, string sid)
    {
        var result = await _execute(["/Query", "/TN", NameForUser(sid), "/XML"]);
        EnsureSuccess(result, "Cannot verify startup task");
        Validate(result.Output, executable, sid, resolveAccount);
    }

    public async Task InstallAsync(string executable, string sid)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Install the agent executable before enabling startup.", executable);
        var existing = await _execute(["/Query", "/TN", NameForUser(sid), "/XML"]);
        if (existing.ExitCode == 0)
        {
            var root = XDocument.Parse(existing.Output).Root;
            var owner = root?.Element(Ns + "Principals")?.Element(Ns + "Principal")?.Element(Ns + "UserId")?.Value;
            if (!SameUser(owner, sid, resolveAccount)) throw new InvalidOperationException("The existing LinkSafe task belongs to another Windows user or could not be resolved. Ask your administrator to resolve it; it was not replaced.");
            var command = root?.Element(Ns + "Actions")?.Element(Ns + "Exec")?.Element(Ns + "Command")?.Value.Trim('"');
            if (!string.Equals(command, executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The existing startup task points to a different installation. Ask your administrator to resolve it; it was not replaced.");
        }
        var temporary = Path.Combine(Path.GetTempPath(), "LinkSafe-startup-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(temporary, Definition(executable, sid));
            var arguments = new List<string> { "/Create", "/TN", NameForUser(sid), "/XML", temporary };
            if (existing.ExitCode == 0) arguments.Add("/F");
            EnsureSuccess(await _execute(arguments.ToArray()), "Startup installation failed. Administrator permission may be needed to replace an older elevated task; use the same Windows account");
            await VerifyAsync(executable, sid);
        }
        finally { File.Delete(temporary); }
    }

    public async Task RemoveAsync(string sid)
    {
        var existing = await _execute(["/Query", "/TN", NameForUser(sid), "/XML"]);
        EnsureSuccess(existing, "Cannot inspect startup task before removal");
        var owner = XDocument.Parse(existing.Output).Root?.Element(Ns + "Principals")?.Element(Ns + "Principal")?.Element(Ns + "UserId")?.Value;
        if (!SameUser(owner, sid, resolveAccount)) throw new InvalidOperationException("Startup belongs to another Windows user or could not be resolved; it was not removed.");
        EnsureSuccess(await _execute(["/Delete", "/TN", NameForUser(sid), "/F"]), "Could not remove startup task");
    }

    private static void EnsureSuccess(SchedulerResult result, string operation)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"{operation} (exit {result.ExitCode}): {result.Error.Trim()} {result.Output.Trim()}");
    }

    private static async Task<SchedulerResult> ExecuteAsync(string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Task Scheduler command.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException("Task Scheduler did not respond. Startup has not been verified.");
        }
        return new(process.ExitCode, await output, await error);
    }
}
