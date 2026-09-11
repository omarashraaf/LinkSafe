using System.Security.Principal;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientSetup;

public sealed partial class MainForm
{
    private readonly Label _startupValue = CopyLabel("Startup task: not checked yet.");
    private DateTimeOffset _lastStartupCheck;
    private bool _checkingStartup;

    private async Task RefreshStartupAsync(ClientAgentConfig config)
    {
        if (_checkingStartup || DateTimeOffset.UtcNow - _lastStartupCheck < TimeSpan.FromSeconds(30)) return;
        _checkingStartup = true;
        try
        {
            if (!config.IsAgentEnabled)
            {
                _startupNotice.Visible = _startupRepairLink.Visible = false;
                _startupValue.Text = "Automatic login backup is not enabled. Click Enable agent to configure it.";
                _startupValue.ForeColor = Muted;
                return;
            }
            await new AgentStartupTask().VerifyAsync(Path.Combine(AgentInstallDirectory, "LinkSafeClientAgent.exe"), WindowsIdentity.GetCurrent().User!.Value);
            if (!new ClientDesktopInstallation().HasTrayStartup())
                throw new InvalidOperationException("Backup startup is configured, but the tray app is not installed for login.");
            _startupValue.Text = "Login startup verified: automatic backup and tray icon. Open LinkSafe Client from the Start menu.";
            _startupValue.ForeColor = Accent;
            _startupNotice.Visible = _startupRepairLink.Visible = false;
        }
        catch (Exception ex)
        {
            _startupValue.Text = "Startup needs attention: " + ex.Message;
            _startupNotice.Visible = _startupRepairLink.Visible = true;
            _startupValue.ForeColor = Warning;
        }
        finally { _lastStartupCheck = DateTimeOffset.UtcNow; _checkingStartup = false; }
    }
}
