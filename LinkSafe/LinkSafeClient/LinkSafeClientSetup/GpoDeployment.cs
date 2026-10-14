using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientSetup;

public sealed record GpoOptions(string ServerUrl, string PackageRoot, string AgentHash, string DeploymentId)
{
    public static GpoOptions Parse(string[] args)
    {
        if (args.Length != 11 || args[0] != "--gpo-install")
            throw new ArgumentException("Expected --gpo-install with server-ip, port, package-root, agent-sha256 and deployment-id.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate deployment option.");
        if (!values.Keys.Order().SequenceEqual(new[] { "--server-ip", "--port", "--package-root", "--agent-sha256", "--deployment-id" }.Order()))
            throw new ArgumentException("Unknown or missing deployment option.");
        if (!IPAddress.TryParse(values["--server-ip"], out var ip) || IPAddress.IsLoopback(ip) ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
            throw new ArgumentException("Use a reachable server IP, not localhost, a URL or a wildcard address.");
        if (!int.TryParse(values["--port"], out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Port must be between 1 and 65535.");
        if (!Regex.IsMatch(values["--agent-sha256"], "^[a-fA-F0-9]{64}$") ||
            !Regex.IsMatch(values["--deployment-id"], "^[A-Za-z0-9._-]{1,64}$"))
            throw new ArgumentException("Invalid package hash or deployment ID.");
        var package = values["--package-root"];
        if (!Path.IsPathFullyQualified(package) || package.StartsWith(@"\\") || package.Contains('"'))
            throw new ArgumentException("Copy the deployment package to a private local folder first.");
        return new(new UriBuilder("http", ip.ToString(), port).Uri.GetLeftPart(UriPartial.Authority),
            Path.GetFullPath(package), values["--agent-sha256"], values["--deployment-id"]);
    }
}

public sealed record GpoDrive(string Root, DriveType Type, bool Ready);
public sealed class GpoReceipt
{
    public string DeploymentId { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public DateTimeOffset AppliedAt { get; set; }
    public List<string> Drives { get; set; } = [];
    public bool ExistingPausePreserved { get; set; }
}

public static class GpoDeployment
{
    private static readonly Version MinimumAgent = new(1, 0, 16, 0);

    public static List<string> SelectDrives(IEnumerable<GpoDrive> drives, string systemDrive) => drives
        .Where(d => d.Ready && d.Type == DriveType.Fixed)
        .Select(d => BackupSourcePaths.TryNormalize(d.Root, out var path) && path.Length == 3 ? path : "")
        .Where(p => p.Length == 3 && !p.Equals(@"C:\", StringComparison.OrdinalIgnoreCase) &&
            !p.Equals(systemDrive.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    // Side effects are injected so rollout semantics can be tested without real tasks, profiles or processes.
    public static async Task<string> ApplyAsync(string root, GpoOptions options, IEnumerable<GpoDrive> drives,
        string systemDrive, Func<Task> prepare, Func<bool, Task> start, Action<string>? progress = null)
    {
        progress?.Invoke("Reading deployment state");
        Directory.CreateDirectory(root);
        var receiptStore = new JsonFileStore<GpoReceipt>(Path.Combine(root, "gpo-deployment.json"));
        var receipt = await receiptStore.ReadAsync(() => new());
        if (receipt.AppliedAt != default && receipt.DeploymentId == options.DeploymentId)
        {
            if (!ClientPolicyStore.SameServer(receipt.ServerUrl, options.ServerUrl))
                throw new InvalidOperationException("This deployment ID belongs to another server. No client was redirected.");
            progress?.Invoke("Already applied; no changes made");
            return "Already applied. Later user selections, disable and stop choices were preserved.";
        }
        var configStore = new JsonFileStore<ClientAgentConfig>(Path.Combine(root, "client-agent-config.json"));
        var config = await configStore.ReadAsync(() => new());
        if (!ClientPolicyStore.SameServer(config.ServerUrl, options.ServerUrl) &&
            !ClientPolicyStore.SameServer(config.ServerUrl, LinkSafeDefaults.DefaultServerUrl))
            throw new InvalidOperationException("This profile is configured for another server. Migration requires an explicit administrator decision.");
        var selectedDrives = SelectDrives(drives, systemDrive);
        progress?.Invoke("Preparing installation");
        await prepare();
        progress?.Invoke("Saving baseline configuration");
        // Re-read after preparation: registration may have updated the identity fields meanwhile.
        config = await configStore.ReadAsync(() => new());
        if (!ClientPolicyStore.SameServer(config.ServerUrl, options.ServerUrl) &&
            !ClientPolicyStore.SameServer(config.ServerUrl, LinkSafeDefaults.DefaultServerUrl))
            throw new InvalidOperationException("Server changed during deployment; configuration was not replaced.");
        if (!ClientPolicyStore.SameServer(config.ServerUrl, options.ServerUrl) || !config.IsAgentEnabled)
        {
            config.ServerUrl = options.ServerUrl;
            config.IsAgentEnabled = true;
            await configStore.SaveAsync(config);
        }
        await new ClientPolicyStore(root).SaveSelectionAsync(new()
        {
            ServerUrl = options.ServerUrl, OverrideServerFolders = true,
            BackupFolders = [.. LinkSafeDefaults.DefaultBackupFolders], CustomFolders = selectedDrives
        });
        var paused = File.Exists(Path.Combine(root, "stop-backup.signal"));
        progress?.Invoke(paused ? "Preserving backup pause" : "Requesting initial backup");
        if (!paused)
        {
            var signal = Path.Combine(root, "run-backup-now.signal");
            // Reuse pending manual work rather than stealing its operation ID.
            if (!File.Exists(signal))
            {
                var temporary = signal + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllTextAsync(temporary, Guid.NewGuid().ToString("D")); File.Move(temporary, signal); }
                catch (IOException) when (File.Exists(signal)) { }
                finally { File.Delete(temporary); }
            }
        }
        progress?.Invoke("Starting installed applications");
        await start(paused);
        progress?.Invoke("Saving deployment receipt");
        await receiptStore.SaveAsync(new()
        {
            DeploymentId = options.DeploymentId, ServerUrl = options.ServerUrl, AppliedAt = DateTimeOffset.UtcNow,
            Drives = selectedDrives, ExistingPausePreserved = paused
        });
        progress?.Invoke("Deployment complete; backup completion not verified");
        return paused ? "Installed and enabled; existing Stop backup pause retained. First backup awaits resume."
            : "Installed and enabled; initial backup requested. See agent/server status for completion, not this receipt.";
    }

    public static int Run(string[] args)
    {
        string? log = null;
        var stage = "Validating invocation and workspace";
        var operation = Guid.NewGuid().ToString("N");
        void Record(string next)
        {
            stage = next;
            // Close each append before proceeding so process termination does not lose buffered stages.
            if (log is not null)
                File.AppendAllText(log, $"{DateTimeOffset.Now:O} [{operation}] {stage}{Environment.NewLine}");
        }
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem || Process.GetCurrentProcess().SessionId == 0 ||
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Run as a non-elevated interactive domain user, not SYSTEM or an administrator token.");
            var options = GpoOptions.Parse(args);
            ClientWorkspace.EnsureCreated();
            var root = LinkSafeDefaults.ClientDataRoot;
            foreach (var name in new[] { ".gpo-deployment.lock", "gpo-deployment.json", "client-agent-config.json",
                         "local-backup-selection.json", "stop-backup.signal", "run-backup-now.signal", "Logs" })
                RestorePathSafety.AssertNoLinks(Path.Combine(root, name));
            Directory.CreateDirectory(LinkSafeDefaults.ClientLogsDirectory);
            log = Path.Combine(LinkSafeDefaults.ClientLogsDirectory, "gpo-install.log");
            RestorePathSafety.AssertNoLinks(log);
            Record($"Setup {typeof(GpoDeployment).Assembly.GetName().Version}; acquiring deployment lock");
            using var gate = new FileStream(Path.Combine(root, ".gpo-deployment.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var bin = Path.Combine(root, "bin");
            RestorePathSafety.AssertNoLinks(bin);
            Directory.CreateDirectory(bin);
            var updatePath = Path.Combine(bin, ".agent-update.lock");
            RestorePathSafety.AssertNoLinks(updatePath);
            Record("Acquiring agent update lock");
            using var update = new FileStream(updatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var desktop = new ClientDesktopInstallation();
            var files = new AgentBinaryStore(bin);
            var sid = identity.User!.Value;
            var drives = new List<GpoDrive>();
            Record("Discovering available drives");
            foreach (var drive in DriveInfo.GetDrives())
            {
                try { drives.Add(new(drive.Name, drive.DriveType, drive.IsReady)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { File.AppendAllText(log, $"{DateTimeOffset.Now:O} Drive {drive.Name} skipped: {ex.Message}{Environment.NewLine}"); }
            }
            var result = ApplyAsync(root, options, drives, Path.GetPathRoot(Environment.SystemDirectory)!, async () =>
            {
                var candidate = Path.Combine(options.PackageRoot, "LinkSafeClientAgent.exe");
                Record("Verifying agent payload");
                VerifyAgent(candidate, options.AgentHash);
                Record("Verifying existing installed payloads before reuse");
                if (files.VersionOf(files.ExecutablePath) >= MinimumAgent)
                    VerifyAgent(files.ExecutablePath, options.AgentHash);
                if (desktop.IsRegistered() && File.Exists(desktop.Executable))
                    VerifySamePayload(Environment.ProcessPath!, desktop.Executable);
                Record("Checking owned worker processes");
                var processes = OwnedProcesses("LinkSafeClientAgent", files.ExecutablePath, sid);
                try
                {
                    if (processes.Count > 0 && files.VersionOf(files.ExecutablePath) < MinimumAgent)
                        throw new IOException("An older worker is running. Close/update it during a maintenance window and retry logon deployment.");
                }
                finally { processes.ForEach(p => p.Dispose()); }
                Record("Checking legacy startup task");
                LegacyClientMigration.RetireOwnedTask(sid);
                if (files.VersionOf(files.ExecutablePath) is not { } current || current < MinimumAgent)
                {
                    if (File.Exists(files.ExecutablePath) && files.VersionOf(files.ExecutablePath) is null)
                        throw new IOException("An unrecognized agent executable exists. Nothing was overwritten.");
                    var copied = Path.Combine(bin, $"LinkSafeClientAgent.{Guid.NewGuid():N}.download");
                    try
                    {
                        Record("Copying and verifying agent payload");
                        File.Copy(candidate, copied);
                        VerifyAgent(copied, options.AgentHash);
                        Record("Installing verified agent payload");
                        await files.ReplaceAsync(copied, MinimumAgent, () => Task.CompletedTask, () => Task.CompletedTask);
                    }
                    finally { File.Delete(copied); }
                }
                Record("Registering user startup task");
                await new AgentStartupTask().InstallAsync(files.ExecutablePath, sid);
                // Existing supported UI can remain running. Deployment does not terminate a user's window.
                Record("Checking installed client UI");
                var source = Environment.ProcessPath!;
                var version = typeof(GpoDeployment).Assembly.GetName().Version!.ToString(3);
                if (desktop.IsRegistered() && File.Exists(desktop.Executable))
                {
                    var info = FileVersionInfo.GetVersionInfo(desktop.Executable);
                    if (!Version.TryParse(info.FileVersion, out var installed) || installed < MinimumAgent ||
                        info.OriginalFilename is not ("LinkSafeClientSetup.dll" or "LinkSafeClientSetup.exe"))
                        throw new IOException("Update the older/unrecognized installed UI before deploying this policy.");
                    source = desktop.Executable;
                    version = installed.ToString(3);
                }
                Record("Installing client UI, shortcuts and tray startup");
                desktop.Install(source, version, enableTray: true);
            }, async paused =>
            {
                Record("Verifying installed payloads before launch");
                VerifyAgent(files.ExecutablePath, options.AgentHash);
                VerifySamePayload(Environment.ProcessPath!, desktop.Executable);
                Record("Checking worker before launch");
                var running = OwnedProcesses("LinkSafeClientAgent", files.ExecutablePath, sid);
                try
                {
                    if (running.Count == 0)
                    {
                        Record("Launching worker");
                        using var agent = StartHidden(files.ExecutablePath, string.Empty);
                        Record("Checking worker startup survival");
                        await Task.Delay(2000);
                        if (agent.HasExited) throw new IOException("Agent exited at startup. Inspect backup-agent.log and retry deployment.");
                    }
                }
                finally { running.ForEach(p => p.Dispose()); }
                Record("Launching tray UI");
                VerifySamePayload(Environment.ProcessPath!, desktop.Executable);
                using var tray = StartHidden(desktop.Executable, "--tray");
            }, Record).GetAwaiter().GetResult();
            Record(result);
            return 0;
        }
        catch (Exception ex)
        {
            try { if (log is not null) File.AppendAllText(log, $"{DateTimeOffset.Now:O} [{operation}] FAILED at {stage}: {ex.GetType().Name} (0x{ex.HResult:X8}) {ex.Message}{Environment.NewLine}"); }
            catch (Exception) { }
            return 1;
        }
    }

    public static void VerifyAgent(string path, string hash)
    {
        RestorePathSafety.AssertNoLinks(path);
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Agent SHA-256 mismatch. No unverified payload was installed.");
        if (new AgentBinaryStore(Path.GetDirectoryName(path)!).VersionOf(path) is not { } version || version < MinimumAgent)
            throw new InvalidDataException("Deployment needs a LinkSafe worker version 1.0.16 or later.");
    }

    public static void VerifySamePayload(string approvedSource, string installedPath)
    {
        RestorePathSafety.AssertNoLinks(approvedSource);
        RestorePathSafety.AssertNoLinks(installedPath);
        using var source = File.OpenRead(approvedSource);
        using var installed = File.OpenRead(installedPath);
        if (!SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(installed)))
            throw new InvalidDataException("Installed client differs from this deployment package. IT must verify the installed release before reuse; no automatic replacement was performed.");
    }

    private static List<Process> OwnedProcesses(string name, string expectedPath, string sid)
    {
        var matches = new List<Process>();
        try
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.HasExited || process.SessionId != Process.GetCurrentProcess().SessionId ||
                        !LegacyClientMigration.ProcessBelongsToUser(process, sid)) continue;
                    if (!string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("This account has an unmanaged agent running. Update that installation before deployment.");
                    matches.Add(process);
                }
                finally { if (!matches.Contains(process)) process.Dispose(); }
            }
            return matches;
        }
        catch { matches.ForEach(p => p.Dispose()); throw; }
    }

    private static Process StartHidden(string path, string argument)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Cannot start installed LinkSafe executable.");
    }
}
