using System.Drawing;

namespace LinkSafeClientSetup;

public sealed partial class MainForm
{
    private static readonly Color Canvas = Color.FromArgb(244, 247, 247);
    private static readonly Color Ink = Color.FromArgb(25, 59, 56);
    private static readonly Color Muted = Color.FromArgb(98, 118, 113);
    private static readonly Color Accent = Color.FromArgb(8, 127, 115);
    private static readonly Color Tint = Color.FromArgb(229, 244, 240);
    private static readonly Color Border = Color.FromArgb(220, 229, 227);
    private static readonly Color Danger = Color.FromArgb(165, 54, 54);
    private static readonly Color Warning = Color.FromArgb(140, 99, 23);
    private readonly Label _connectionValue = CopyLabel("Checking connection...", bold: true);
    private readonly Label _agentValue = CopyLabel("Checking agent...", bold: true);
    private readonly Label _lastBackupValue = CopyLabel("No backups yet", bold: true);
    private readonly Label _foldersValue = CopyLabel("Waiting for your server policy...");
    private readonly Label _drivesValue = CopyLabel("None selected");
    private readonly Label _restoreValue = CopyLabel("No restore reported yet.");
    private readonly Label _registrationValue = CopyLabel("Registration is handled in the background.");
    private readonly Label _errorValue = CopyLabel(string.Empty);

    private void BuildDashboard()
    {
        SuspendLayout();
        Text = "LinkSafe Client";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10F);
        ForeColor = Ink;
        BackColor = Canvas;
        ClientSize = new Size(760, Math.Min(660, (Screen.PrimaryScreen?.WorkingArea.Height ?? 900) - 100));
        MinimumSize = new Size(660, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(26, 20, 26, 20) };
        var content = Stack();
        content.Dock = DockStyle.Top;
        content.BackColor = Canvas;
        content.Margin = Padding.Empty;

        var header = Stack();
        header.BackColor = Canvas;
        var brand = CopyLabel("LinkSafe", bold: true);
        brand.Font = new Font("Trebuchet MS", 24, FontStyle.Bold);
        brand.ForeColor = Accent;
        AddRow(header, brand);
        AddRow(header, CopyLabel($"{Environment.MachineName}  /  {Environment.UserName}"));
        AddRow(content, header);

        var server = Card("Connection & details", "");
        var serverRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 4) };
        serverRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        serverRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _serverUrlTextBox.AccessibleName = "Server IP or address";
        _serverUrlTextBox.PlaceholderText = "For example, 10.0.0.102";
        _serverUrlTextBox.Font = new Font("Segoe UI", 11);
        _serverUrlTextBox.BorderStyle = BorderStyle.None;
        _serverUrlTextBox.BackColor = Color.White;
        _serverUrlTextBox.ForeColor = Ink;
        _serverUrlTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _serverUrlTextBox.Dock = DockStyle.None;
        _serverInputFrame = new Panel { Height = 40, Dock = DockStyle.Fill, MinimumSize = new Size(0, 40), BackColor = Color.White, Margin = new Padding(0, 0, 10, 0) };
        _serverInputFrame.Paint += (_, e) => { using var pen = new Pen(Border); e.Graphics.DrawRectangle(pen, 0, 0, _serverInputFrame.Width - 1, _serverInputFrame.Height - 1); };
        _serverInputFrame.Controls.Add(_serverUrlTextBox);
        _serverInputFrame.Resize += (_, _) =>
        {
            var inset = Math.Max(8, (int)(10 * DeviceDpi / 96F));
            _serverUrlTextBox.SetBounds(inset, Math.Max(0, (_serverInputFrame.ClientSize.Height - _serverUrlTextBox.PreferredHeight) / 2),
                Math.Max(0, _serverInputFrame.ClientSize.Width - inset * 2), _serverUrlTextBox.PreferredHeight);
        };
        serverRow.Controls.Add(_serverInputFrame, 0, 0);
        _testButton = CreateButton("Test connection", async (_, _) => await RunUtilityAsync("Testing", TestServerAsync));
        serverRow.Controls.Add(_testButton, 1, 0);
        AddRow(server, CopyLabel("Server IP or address", bold: true));
        AddRow(server, serverRow);
        AddRow(server, _registrationValue);
        AddRow(server, _startupValue);
        _startupRepairLink.LinkClicked += async (_, _) =>
        { if (CanRunAction("Enable agent")) await RunUtilityAsync("Enabling", EnableAgentAsync); };
        AddRow(server, _startupRepairLink);
        AddRow(server, _policyValue);
        AddRow(server, _restoreValue);
        AddRow(server, _statusLabel);
        _askBeforeRestoreCheckBox.Text = "Ask before restoring data";
        _askBeforeRestoreCheckBox.Margin = new Padding(0, 8, 0, 12);
        AddRow(server, _askBeforeRestoreCheckBox);
        var logs = new LinkLabel { Text = "Open logs", AccessibleName = "Open logs", AutoSize = true, LinkColor = Accent, Margin = new Padding(0, 4, 0, 0) };
        logs.LinkClicked += (_, _) => OpenLogs();
        AddRow(server, logs);
        _detailsPanel = server;
        server.Visible = false;

        var status = Card("Backup", "");
        var metrics = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, Margin = new Padding(0, 4, 0, 14) };
        foreach (var item in new[] { ("Connection", _connectionValue), ("Agent", _agentValue), ("Last successful backup", _lastBackupValue) })
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
            var metric = Stack();
            metric.Padding = new Padding(10);
            metric.BackColor = Canvas;
            metric.Margin = new Padding(0, 0, 8, 0);
            AddRow(metric, CopyLabel(item.Item1));
            AddRow(metric, item.Item2);
            metrics.Controls.Add(metric);
        }
        AddRow(status, metrics);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty, WrapContents = true };
        _backupActionButton = CreateButton("Back up now", async (_, _) => await ToggleBackupAsync());
        var backupButton = _backupActionButton;
        backupButton.BackColor = Accent;
        backupButton.ForeColor = Color.White;
        backupButton.FlatAppearance.BorderColor = Accent;
        backupButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(6, 107, 96);
        actions.Controls.Add(backupButton);
        _agentActionButton = CreateButton("Enable agent", async (_, _) => await ToggleAgentAsync());
        actions.Controls.Add(_agentActionButton);
        AddRow(status, actions);
        StyleBodyLabel(_progressLabel);
        _progressLabel.Visible = true;
        _progressLabel.Text = "Checking status...";
        _progressLabel.ForeColor = Accent;
        _progressLabel.Margin = new Padding(0, 14, 0, 6);
        AddRow(status, _progressLabel);
        _progressBar.Height = 7;
        _progressBar.Margin = new Padding(0, 0, 0, 8);
        _progressBar.MarqueeAnimationSpeed = 30;
        AddRow(status, _progressBar);
        StyleBodyLabel(_statusLabel);
        _statusLabel.Text = "No activity reported yet.";
        _errorValue.ForeColor = Danger;
        _errorValue.Visible = false;
        AddRow(status, _errorValue);
        _startupNotice.ForeColor = Warning;
        _startupNotice.Visible = false;
        AddRow(status, _startupNotice);
        AddRow(content, status);

        var sources = Card("Your folders", "");
        AddRow(sources, _foldersValue);
        AddRow(sources, _drivesValue);
        var sourceActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 0), WrapContents = true };
        _foldersButton = CreateButton("Choose folders", async (_, _) => await ChooseBackupFoldersAsync());
        _restoreButton = CreateButton("Restore data", (_, _) => _restoreMenu.Show(_restoreButton!, new Point(0, _restoreButton!.Height)));
        _restoreMenu.Items.Add("Restore missing files", null, async (_, _) =>
        { if (CanRunAction("Restore data")) await RestoreDataAsync(promptIfMissing: true, forcePrompt: true); });
        _restoreMenu.Items.Add("Move data from another device", null, async (_, _) =>
        { if (CanRunAction("Restore data")) await RestoreDataAsync(promptIfMissing: true, forcePrompt: true, fromAnotherDevice: true); });
        sourceActions.Controls.Add(_foldersButton);
        sourceActions.Controls.Add(_restoreButton);
        AddRow(sources, sourceActions);
        AddRow(content, sources);

        var footer = Stack();
        footer.BackColor = Canvas;
        var details = new LinkLabel { Text = "Connection & details", AccessibleName = "Connection & details", UseMnemonic = false, AutoSize = true, LinkColor = Accent, Margin = new Padding(0, 0, 0, 12) };
        details.LinkClicked += (_, _) => { server.Visible = !server.Visible; details.Text = server.Visible ? "Hide details" : "Connection & details"; };
        AddRow(footer, details);
        AddRow(footer, CopyLabel("Closing this window keeps LinkSafe in the tray."));
        AddRow(content, footer);
        AddRow(content, server);
        scroll.Controls.Add(content);
        Controls.Add(scroll);
        UpdateActionButtons();
        ResumeLayout(true);
    }

    private static TableLayoutPanel Stack() => new()
    {
        ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top, BackColor = Color.White,
        ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) }, Margin = new Padding(0, 0, 0, 16)
    };

    private static TableLayoutPanel Card(string title, string description)
    {
        var card = Stack();
        card.Padding = new Padding(20, 16, 20, 16);
        card.Paint += (_, e) => { using var pen = new Pen(Border); e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
        var heading = CopyLabel(title, bold: true);
        heading.Font = new Font("Trebuchet MS", 14, FontStyle.Bold);
        AddRow(card, heading);
        if (!string.IsNullOrWhiteSpace(description)) AddRow(card, CopyLabel(description));
        return card;
    }

    private static Label CopyLabel(string text, bool bold = false)
    {
        var label = new Label { Text = text };
        StyleBodyLabel(label);
        if (bold) { label.Font = new Font("Segoe UI", 10, FontStyle.Bold); label.ForeColor = Ink; }
        return label;
    }

    private static void StyleBodyLabel(Label label)
    {
        label.AutoSize = true;
        label.Dock = DockStyle.Fill;
        label.Margin = new Padding(0, 0, 0, 8);
        label.ForeColor = Muted;
        label.UseMnemonic = false;
    }

    private static void AddRow(TableLayoutPanel panel, Control control)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(control, 0, row);
    }

    private static string FriendlyFolders(string folders) => folders
        .Replace("AppDataRoaming", "App data (Roaming)")
        .Replace("ChromeProfile", "Chrome profile")
        .Replace("EdgeProfile", "Edge profile")
        .Replace("FirefoxProfile", "Firefox profile");

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _refreshTimer.Dispose(); _notifyIcon.Dispose(); _restoreMenu.Dispose(); }
        base.Dispose(disposing);
    }
}
