using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LinkSafeServerSetup;

internal sealed class SetupForm : Form
{
    private static readonly Color Ink = Color.FromArgb(24, 52, 59);
    private static readonly Color Teal = Color.FromArgb(0, 116, 112);
    private readonly TextBox _backup = new() { Text = @"C:\LinkSafeBackups", Dock = DockStyle.Fill, AccessibleName = "Backup storage folder" };
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Value = 5055, Width = 130, AccessibleName = "Server port" };
    private readonly Button _install = ActionButton("1. Install server", true);
    private readonly Button _open = ActionButton("2. Create admin account");
    private readonly Button _lan = ActionButton("3. Enable LAN access");
    private readonly Label _status = new() { Text = "Ready to install", AutoSize = true, ForeColor = Teal, Dock = DockStyle.Fill };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Installation details" };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 8, Visible = false };
    private bool _busy;
    private readonly string _helper = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LinkSafe Server", "SetupTools", "Manage-LinkSafeServer.ps1");

    internal SetupForm()
    {
        Text = "LinkSafe | Server Setup 1.0.21";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Font = new Font("Segoe UI", 10);
        ForeColor = Ink;
        BackColor = Color.FromArgb(243, 248, 247);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(740, 690);
        ClientSize = new Size(820, 730);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30), ColumnCount = 1, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(TextLabel("LINKSAFE  /  SERVER", 11, Teal, true), 0, 0);
        layout.Controls.Add(TextLabel("One home for your team's backups", 24, Ink, true), 0, 1);
        layout.Controls.Add(TextLabel("Runs in the background as a Windows service, even after you close Setup or restart the server. No separate .NET installation is needed.", 10, Ink), 0, 2);
        var fields = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, Padding = new Padding(16), BackColor = Color.White, Margin = new Padding(0, 15, 0, 15) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.Controls.Add(TextLabel("Backup location", 10, Ink), 0, 0);
        fields.Controls.Add(_backup, 1, 0);
        var browse = ActionButton("Choose folder");
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Choose a new or empty backup folder on a fixed NTFS drive", UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) _backup.Text = dialog.SelectedPath; };
        fields.Controls.Add(browse, 2, 0);
        fields.Controls.Add(TextLabel("Network port", 10, Ink), 0, 1);
        fields.Controls.Add(_port, 1, 1);
        var location = TextLabel(@"Application: C:\Program Files\LinkSafe Server" + Environment.NewLine + "Updates keep the existing backup location, port and settings.", 9, Color.FromArgb(82, 104, 109));
        fields.Controls.Add(location, 0, 2);
        fields.SetColumnSpan(location, 3);
        layout.Controls.Add(fields, 0, 3);
        layout.Controls.Add(TextLabel("Start safely, then connect your devices", 13, Ink, true), 0, 4);
        layout.Controls.Add(TextLabel("First create your admin account on this server. Then enable access by IP on your trusted local network. Setup never copies users, passwords or backups from another server.", 10, Ink), 0, 5);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 12) };
        actions.Controls.AddRange([_install, _open, _lan]);
        layout.Controls.Add(actions, 0, 6);
        var state = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(0, 0, 0, 8) };
        state.Controls.Add(_status);
        state.Controls.Add(_progress);
        layout.Controls.Add(state, 0, 7);
        layout.Controls.Add(_log, 0, 8);
        layout.Controls.Add(TextLabel("Backups and configuration are kept when uninstalling. HTTP is for a trusted LAN only; do not expose this server to the internet.", 9, Color.FromArgb(82, 104, 109)), 0, 9);
        Controls.Add(layout);
        _install.Click += async (_, _) => await ExecuteAsync(InstallAsync, "Installing LinkSafe Server...");
        _open.Click += async (_, _) => await ExecuteAsync(() => RunScriptAsync(_helper, "OpenUi"), "Opening local admin setup...");
        _lan.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Enable HTTP access on this computer's IP for the local subnet?\n\nOnly Domain and Private firewall profiles will be allowed. Use a trusted LAN, not the internet. Create your admin account first.", "Enable LAN access", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await ExecuteAsync(() => RunScriptAsync(_helper, "EnableLan"), "Enabling trusted LAN access...");
        };
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Please wait for this setup operation to finish."; } };
        RefreshActions();
    }

    private static Label TextLabel(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(1100, 0),
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, Margin = new Padding(0, 5, 0, 8)
    };

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 36), FlatStyle = FlatStyle.Flat, Padding = new Padding(9, 3, 9, 3), Margin = new Padding(0, 0, 8, 0), BackColor = primary ? Teal : Color.White, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = primary ? Teal : Color.FromArgb(199, 218, 217);
        return button;
    }

    private void RefreshActions()
    {
        var installed = File.Exists(_helper);
        _install.Enabled = !_busy;
        _install.Text = installed ? "1. Install / update" : "1. Install server";
        _open.Enabled = _lan.Enabled = installed && !_busy;
        _backup.Enabled = _port.Enabled = !installed && !_busy;
    }

    private async Task ExecuteAsync(Func<Task> operation, string description)
    {
        _busy = true;
        _status.ForeColor = Teal;
        _status.Text = description;
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Marquee;
        RefreshActions();
        try { await operation(); _status.Text = "Step complete. See details below for the next step."; }
        catch (Exception e) { _status.ForeColor = Color.FromArgb(172, 51, 51); _status.Text = "This step could not finish. See details below."; _log.AppendText(e.Message + Environment.NewLine); }
        finally { _busy = false; _progress.Style = ProgressBarStyle.Blocks; _progress.Visible = false; RefreshActions(); }
    }

    private async Task InstallAsync()
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LinkSafe Server Setup Cache");
        AssertNoLinks(cache);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        Directory.CreateDirectory(cache);
        new DirectoryInfo(cache).SetAccessControl(security);
        var stage = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var helper = Path.Combine(stage, "Manage-LinkSafeServer.ps1");
            using (var source = Payload.Resource("LinkSafe.Manage.ps1"))
            await using (var target = File.Create(helper)) { await source.CopyToAsync(target); }
            await RunScriptAsync(helper, "Validate");
            _log.AppendText("Extracting and verifying the bundled server and client apps..." + Environment.NewLine);
            var payloadRoot = Path.Combine(stage, "Payload");
            await Task.Run(() => Payload.Extract(payloadRoot));
            await RunScriptAsync(helper, "Install", payloadRoot);
        }
        finally
        {
            // Cleanup is restricted to the unique directory created for this run.
            if (Path.GetDirectoryName(Path.GetFullPath(stage)) == Path.GetFullPath(cache))
            {
                try { AssertNoLinks(stage); Directory.Delete(stage, recursive: true); }
                catch (Exception e) { _log.AppendText("Temporary setup files retained: " + e.Message + Environment.NewLine); }
            }
        }
    }

    private static void AssertNoLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Setup does not use linked or redirected application directories.");
    }

    private async Task RunScriptAsync(string script, string mode, string? payload = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Mode", mode, "-BackupRoot", _backup.Text.Trim(), "-Port", _port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        if (payload != null) { start.ArgumentList.Add("-PayloadRoot"); start.ArgumentList.Add(payload); }
        using var process = Process.Start(start) ?? throw new IOException("Could not start the installation helper.");
        async Task ReadOutputAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line) _log.AppendText(line + Environment.NewLine);
        }
        await Task.WhenAll(ReadOutputAsync(process.StandardOutput), ReadOutputAsync(process.StandardError), process.WaitForExitAsync());
        if (process.ExitCode != 0) throw new InvalidOperationException("Setup stopped safely. Resolve the error above and try again. Your backup data has not been removed.");
    }
}
