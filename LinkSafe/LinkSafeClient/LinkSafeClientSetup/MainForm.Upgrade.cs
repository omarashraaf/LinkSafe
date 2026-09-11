using System.Diagnostics;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientSetup;

public sealed partial class MainForm
{
    private static readonly Version RequiredAgentVersion = new(1, 0, 16, 0);
    private bool _upgrading;

    private async Task CheckInstalledAgentOnOpenAsync()
    {
        if (!File.Exists(Path.Combine(AgentInstallDirectory, "LinkSafeClientAgent.exe"))) return;
        try { await EnsureAgentBinaryAsync(NormalizeServerUrl(_serverUrlTextBox.Text)); }
        catch (Exception ex) { ShowError("Agent update check: " + ex.Message); }
    }

    private async Task<string> UpdateManagedAgentAsync(string serverUrl)
    {
        if (_upgrading) throw new InvalidOperationException("An agent update is already in progress.");
        _upgrading = true;
        var files = new AgentBinaryStore(AgentInstallDirectory);
        string? downloaded = null;
        string? ownPause = null;
        var restart = false;
        FileStream? updateLock = null;
        try
        {
            Directory.CreateDirectory(AgentInstallDirectory);
            RestorePathSafety.AssertNoLinks(AgentInstallDirectory);
            var lockPath = Path.Combine(AgentInstallDirectory, ".agent-update.lock");
            RestorePathSafety.AssertNoLinks(lockPath);
            updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = files.VersionOf(files.ExecutablePath);
            if (current >= RequiredAgentVersion)
            {
                await LegacyClientMigration.RetireAsync();
                files.CleanupOlderCopies();
                return files.ExecutablePath;
            }
            ShowProgress("Downloading the current agent before replacing the older version...");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(serverUrl.TrimEnd('/') + "/api/downloads/client-agent", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            downloaded = Path.Combine(AgentInstallDirectory, $"LinkSafeClientAgent.{Guid.NewGuid():N}.download");
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = new FileStream(downloaded, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target);
            if (files.VersionOf(downloaded) is not { } candidate || candidate < RequiredAgentVersion)
                throw new InvalidOperationException("Update the server downloads to agent 1.0.16 first. The installed agent has not been removed.");

            await LegacyClientMigration.RetireAsync();

            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            var running = HasManagedAgentProcess(files.ExecutablePath);
            restart = running || config.IsAgentEnabled;
            if (running)
            {
                if (current is null || current < new Version(1, 0, 6, 0))
                    throw new InvalidOperationException("This older running agent cannot pause safely for automatic replacement. Sign out/exit it once, then open this client again. No files were removed.");
                var stop = new BackupStopControl(LinkSafeDefaults.ClientStopBackupSignalPath);
                var stopId = await stop.ReadAsync();
                if (string.IsNullOrWhiteSpace(stopId))
                {
                    ownPause = stopId = Guid.NewGuid().ToString("D");
                    var temporary = LinkSafeDefaults.ClientStopBackupSignalPath + "." + stopId + ".tmp";
                    await File.WriteAllTextAsync(temporary, stopId);
                    File.Move(temporary, LinkSafeDefaults.ClientStopBackupSignalPath, true);
                }
                ShowPending("Waiting for the old agent to pause safely before updating...");
                var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
                while ((await _statusStore.LoadAsync(() => new StatusFileModel())).LastStopRequestId != stopId)
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                        throw new InvalidOperationException("Agent has not paused yet. Close any restore prompt and try again. The old version was kept.");
                    await Task.Delay(500);
                }
                await StopManagedAgentProcessAsync(files.ExecutablePath);
            }
            ShowProgress("Installing the updated agent. Configuration and queued backups are preserved...");
            await files.ReplaceAsync(downloaded, RequiredAgentVersion,
                async () => { if (restart) await StartManagedAgentAsync(files.ExecutablePath); },
                () => StopManagedAgentProcessAsync(files.ExecutablePath));
            downloaded = null;
            files.CleanupOlderCopies();
            ShowCompleted("Agent updated. The previous installed version was removed; your settings and queue were kept.");
            return files.ExecutablePath;
        }
        catch
        {
            if (restart && File.Exists(files.ExecutablePath) && !HasManagedAgentProcess(files.ExecutablePath))
                await StartManagedAgentAsync(files.ExecutablePath);
            throw;
        }
        finally
        {
            try
            {
                if (downloaded is not null) File.Delete(downloaded);
                if (ownPause is not null && await new BackupStopControl(LinkSafeDefaults.ClientStopBackupSignalPath).ReadAsync() == ownPause)
                {
                    File.Delete(LinkSafeDefaults.ClientStopBackupSignalPath);
                    if (restart && !File.Exists(LinkSafeDefaults.ClientRunBackupSignalPath))
                    {
                        var signal = LinkSafeDefaults.ClientRunBackupSignalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        await File.WriteAllTextAsync(signal, Guid.NewGuid().ToString("D"));
                        File.Move(signal, LinkSafeDefaults.ClientRunBackupSignalPath, true);
                    }
                }
            }
            finally { updateLock?.Dispose(); _upgrading = false; UpdateActionButtons(); }
        }
    }

    private static bool HasManagedAgentProcess(string path)
    {
        var found = false;
        foreach (var process in Process.GetProcessesByName("LinkSafeClientAgent"))
        using (process)
        {
            if (process.HasExited) continue;
            try
            {
                if (process.SessionId != Process.GetCurrentProcess().SessionId) continue;
                if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Another agent instance is outside this user's managed installation. Close it before updating; LinkSafe will not delete or stop it automatically.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("The older agent may be elevated. Run this client as administrator using the same Windows account for this one-time upgrade, then click Enable agent to repair login startup. The running agent was not stopped.", ex);
            }
            found = true;
        }
        return found;
    }

    private static async Task StopManagedAgentProcessAsync(string path)
    {
        foreach (var process in Process.GetProcessesByName("LinkSafeClientAgent"))
        using (process)
        {
            if (process.HasExited || process.SessionId != Process.GetCurrentProcess().SessionId ||
                !string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) continue;
            process.Kill();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
        }
    }

    private static async Task StartManagedAgentAsync(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, Arguments = "--skip-restore-prompt=true"
        }) ?? throw new IOException("The updated agent could not start.");
        await Task.Delay(2000);
        if (process.HasExited) throw new IOException("Agent exited during startup. The previous binary will be restored.");
    }
}
