using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientSetup;

public sealed partial class MainForm
{
    private bool _uninstalling;
    internal void OpenFromLauncher() => RestoreFromTray();
    internal async void UninstallFromLauncher() => await UninstallClientAsync();

    private async Task UpdateInstalledWindowAsync()
    {
        var desktop = new ClientDesktopInstallation();
        var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
        if (!config.IsAgentEnabled && !desktop.IsRegistered()) return;
        try
        {
            desktop.Install(Environment.ProcessPath!, typeof(MainForm).Assembly.GetName().Version!.ToString(3), config.IsAgentEnabled);
            _lastStartupCheck = DateTimeOffset.MinValue;
        }
        catch (Exception ex) { ShowError("Client installation needs attention: " + ex.Message); }
    }

    private async Task UninstallClientAsync()
    {
        if (_uninstalling) return;
        RestoreFromTray();
        if (_upgrading || BackupActive || _restoreRequested || _stopRequested || _pendingAction is not null)
        {
            ShowError("Wait for the current operation to finish, or stop backup, before uninstalling.");
            return;
        }
        if (MessageBox.Show(this, "Uninstall LinkSafe Client for this Windows user? The background agent and login startup will stop. Configuration, logs, queued backups and all server backups will be kept.",
            "Uninstall LinkSafe Client", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _uninstalling = true;
        UpdateActionButtons();
        _refreshTimer.Stop();
        try
        {
            var desktop = new ClientDesktopInstallation();
            desktop.AssertRegistered();
            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            var path = Path.Combine(AgentInstallDirectory, "LinkSafeClientAgent.exe");
            // Refuse to affect another session or an unmanaged agent installation.
            HasManagedAgentProcess(path);
            if (config.IsAgentEnabled) await UninstallScheduledTaskAsync();
            config.IsAgentEnabled = false;
            await _configStore.WriteAsync(config);
            await StopManagedAgentProcessAsync(path);
            RestorePathSafety.AssertNoLinks(path);
            if (File.Exists(path))
            {
                if (new AgentBinaryStore(AgentInstallDirectory).VersionOf(path) is null)
                    throw new IOException("The installed agent executable could not be verified; it was not removed.");
                File.Delete(path);
            }
            desktop.RemoveRegistration();
            desktop.ScheduleExecutableCleanup();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError("Uninstall needs attention: " + ex.Message + " Your backup data has been kept.");
            _refreshTimer.Start();
        }
        finally { _uninstalling = false; UpdateActionButtons(); }
    }
}
