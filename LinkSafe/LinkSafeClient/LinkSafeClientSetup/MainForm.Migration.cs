using Shared.Models;

namespace LinkSafeClientSetup;

public sealed partial class MainForm
{
    private RoamingRestoreCandidate? ChooseMigrationDevice(IReadOnlyList<RoamingRestoreCandidate> devices)
    {
        using var dialog = new Form
        {
            Text = "Move data to this device", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(580, 220), FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, BackColor = Canvas, Font = Font
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 3, ColumnCount = 1 };
        layout.Controls.Add(new Label { Text = "Choose the device whose backup you want to copy here. Existing files and source backups will be kept.",
            Dock = DockStyle.Fill, AutoSize = true, ForeColor = Ink }, 0, 0);
        var choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Source device" };
        foreach (var device in devices)
            choice.Items.Add($"{device.SourceComputerName} - backup {(device.LastBackupTime ?? device.LastSeenAt).ToLocalTime():g}");
        choice.SelectedIndex = 0;
        layout.Controls.Add(choice, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var next = new Button { Text = "Compare files", AutoSize = true, DialogResult = DialogResult.OK, BackColor = Accent, ForeColor = Color.White };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(next);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = next;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? devices[choice.SelectedIndex] : null;
    }
}
