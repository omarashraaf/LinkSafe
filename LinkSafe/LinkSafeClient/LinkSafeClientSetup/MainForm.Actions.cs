using Shared.Models;

namespace LinkSafeClientSetup;

public sealed partial class MainForm
{
    private Button? _backupActionButton, _agentActionButton, _foldersButton, _restoreButton, _testButton;
    private Panel _serverInputFrame = null!;
    private TableLayoutPanel _detailsPanel = null!;
    private readonly ContextMenuStrip _restoreMenu = new();
    private readonly Label _startupNotice = CopyLabel("Login startup needs attention. Open details to repair it.");
    private readonly LinkLabel _startupRepairLink = new() { Text = "Repair startup", AccessibleName = "Repair startup", AutoSize = true, LinkColor = Accent, Visible = false };
    private bool _stateLoaded, _agentEnabled, _reportedBackupActive;
    private string? _pendingAction;
    private DateTime _lastUiFeedbackAt;

    private bool BackupActive => _backupRequested || _reportedBackupActive;

    private async Task ToggleBackupAsync()
    {
        if (!_stateLoaded || _stopRequested || _upgrading || _restoreRequested || _uninstalling || _pendingAction is not null) return;
        if (BackupActive) await StopBackupAsync();
        else await RunBackupNowAsync();
    }

    private async Task ToggleAgentAsync()
    {
        if (!_stateLoaded || BackupActive || _stopRequested || _upgrading || _restoreRequested || _uninstalling || _pendingAction is not null) return;
        await RunUtilityAsync(_agentEnabled ? "Disabling" : "Enabling", _agentEnabled ? DisableAgentAsync : EnableAgentAsync);
    }

    private async Task RunUtilityAsync(string operation, Func<Task> action)
    {
        if (_pendingAction is not null) return;
        _pendingAction = operation;
        UpdateActionButtons();
        try { await action(); }
        finally { _pendingAction = null; UpdateActionButtons(); }
    }

    private void UpdateActionButtons()
    {
        if (_backupActionButton is null || _agentActionButton is null) return;
        var blocked = !_stateLoaded || _upgrading || _restoreRequested || _uninstalling || _pendingAction is not null;
        SetAction(_backupActionButton, _stopRequested ? "Stopping..." : BackupActive ? "Stop backup" : "Back up now", !blocked && !_stopRequested);
        _backupActionButton.BackColor = _backupActionButton.FlatAppearance.BorderColor = BackupActive ? Danger : Accent;
        _backupActionButton.FlatAppearance.MouseOverBackColor = BackupActive ? Color.FromArgb(140, 40, 40) : Color.FromArgb(6, 107, 96);
        SetAction(_agentActionButton, _pendingAction is "Enabling" or "Disabling" ? _pendingAction + "..." : _agentEnabled ? "Disable agent" : "Enable agent",
            !blocked && !BackupActive && !_stopRequested);
        foreach (var button in new[] { _foldersButton, _restoreButton, _testButton })
            if (button is not null) button.Enabled = !blocked && !BackupActive && !_stopRequested;
        _startupRepairLink.Enabled = !blocked && !BackupActive && !_stopRequested;
    }

    private static void SetAction(Button button, string text, bool enabled)
    {
        button.Text = button.AccessibleName = text;
        button.Enabled = enabled;
    }

    private void UpdateReportedActions(StatusFileModel status)
    {
        _stateLoaded = true;
        _agentEnabled = status.AgentEnabled;
        _reportedBackupActive = status.LastOperationStatus is "Checking" or "Packaging" or "Uploading" or "Running";
        UpdateActionButtons();
    }
}
