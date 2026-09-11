using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientSetup;

public sealed partial class MainForm
{
    private readonly ClientPolicyStore _policyStore = new();
    private readonly Label _policyValue = CopyLabel("Waiting for policy synchronization...");

    private async Task ChooseBackupFoldersAsync()
    {
        try
        {
            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            await _policyStore.ApplyAsync(config);
            var selection = await _policyStore.GetSelectionAsync();
            using var dialog = BuildFolderDialog(config,
                selection.OverrideServerFolders && ClientPolicyStore.SameServer(config.ServerUrl, selection.ServerUrl),
                out var useServer, out var folders, out var custom);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await _policyStore.SaveSelectionAsync(new()
            {
                ServerUrl = config.ServerUrl,
                OverrideServerFolders = !useServer.Checked,
                BackupFolders = folders.CheckedItems.Cast<string>().ToList(),
                CustomFolders = custom.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
            });
            ShowCompleted("Folder selection saved. The enabled agent will scan it after current work finishes. If paused, click Back up now.");
            await RefreshStatusAsync();
        }
        catch (Exception ex) { ShowError($"Could not save folder selection: {ex.Message}"); }
    }

    private static Form BuildFolderDialog(ClientAgentConfig config, bool localOverride,
        out CheckBox useServer, out CheckedListBox folders, out TextBox custom)
    {
        var dialog = new Form
        {
            Text = "Choose backup folders", Font = new Font("Segoe UI", 10), BackColor = Canvas,
            ForeColor = Ink, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            MinimizeBox = false, MaximizeBox = false, MinimumSize = new Size(540, 560), Size = new Size(650, 690)
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var server = new CheckBox { Text = "Use server settings", Checked = !localOverride, AutoSize = true, AccessibleName = "Use server settings" };
        layout.Controls.Add(server);
        layout.Controls.Add(CopyLabel("Uncheck to choose folders for this PC instead. Server exclusions, size limits and timing still apply."));
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, AccessibleName = "User folders", BorderStyle = BorderStyle.FixedSingle };
        foreach (var folder in LinkSafeDefaults.KnownFolders) list.Items.Add(folder, config.BackupFolders.Contains(folder));
        layout.Controls.Add(list);
        layout.Controls.Add(CopyLabel("Additional local folders or drives (one path per line, for example D:\\Projects or E:\\)"));
        var paths = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "Additional folder paths", Lines = config.CustomFolders.ToArray() };
        layout.Controls.Add(paths);
        var browse = new Button { Text = "Add folder or drive...", AutoSize = true, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Choose a folder or drive to back up", UseDescriptionForTitle = true };
            if (picker.ShowDialog(dialog) == DialogResult.OK)
                paths.Lines = paths.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).Append(picker.SelectedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        };
        layout.Controls.Add(browse);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Save selection", AutoSize = true, BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        save.Click += (_, _) =>
        {
            if (!server.Checked && paths.Lines.Any(line => !string.IsNullOrWhiteSpace(line) && !BackupSourcePaths.TryNormalize(line, out _)))
            { MessageBox.Show(dialog, "Use full local paths without wildcards or '..', for example D:\\Projects."); return; }
            if (server.Checked) paths.Clear();
            dialog.DialogResult = DialogResult.OK;
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions);
        void UpdateEnabled() { list.Enabled = paths.Enabled = browse.Enabled = !server.Checked; }
        server.CheckedChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(layout);
        useServer = server; folders = list; custom = paths;
        return dialog;
    }

    private async Task RefreshPolicyLabelAsync(ClientAgentConfig config)
    {
        var cached = await _policyStore.GetPolicyAsync();
        var selection = await _policyStore.GetSelectionAsync();
        var local = selection.OverrideServerFolders && ClientPolicyStore.SameServer(config.ServerUrl, selection.ServerUrl);
        var valid = ClientPolicyStore.SameServer(config.ServerUrl, cached.ServerUrl) && cached.Policy is not null;
        var source = local ? "This PC's selection overrides server folders" :
            valid && cached.Policy!.IsClientOverride ? "Server: per-client selection" : "Server: default selection";
        _policyValue.Text = $"{source}\nLast server policy sync: {(valid ? cached.SyncedAt?.ToLocalTime().ToString("g") : "Not yet synced")}\nChanges scan after current work finishes; paused backups stay paused.";
    }
}
