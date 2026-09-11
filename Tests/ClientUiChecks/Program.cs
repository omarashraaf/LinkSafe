using System.Reflection;
using System.Drawing.Imaging;
using LinkSafeClientSetup;
using Shared.Models;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = (MainForm)Activator.CreateInstance(typeof(MainForm), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [false], null)!;
        // The presentation-only constructor has no timers, tray icon, network calls or disk writes.
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.ShowInTaskbar = false;
        form.Show();
        var status = new StatusFileModel { ServerReachable = true, AgentEnabled = true, Registered = true,
            LastSuccessfulBackup = new DateTimeOffset(2026, 9, 9, 16, 0, 0, TimeSpan.FromHours(3)),
            LastOperationStatus = "NoChanges", LastOperationMessage = "No new or changed files were found.",
            AskBeforeRestoreEveryTime = true };
        var config = new ClientAgentConfig { BackupFolders = ["Desktop", "Documents", "Downloads", "AppDataRoaming"], CustomFolders = [@"D:\Projects", @"E:\Accounts"] };
        Invoke("UpdateStatusLabel", status, config);
        Field<TextBox>("_serverUrlTextBox").Text = "http://10.0.0.102:5055";
        Check(Field<Label>("_connectionValue").Text == "Connected", "Connection status");
        Check(Field<Label>("_agentValue").Text == "Enabled", "Agent status");
        Check(Field<Label>("_foldersValue").Text.Contains("App data (Roaming)"), "Friendly folder names");
        Check(Field<Label>("_drivesValue").Text.Contains(@"E:\Accounts"), "Additional drive paths");
        var buttons = Descendants(form).OfType<Button>().ToList();
        Check(buttons.Count == 5 && buttons.All(b => b.TabStop && b.AccessibleName == b.Text), "Five accessible buttons replace nine competing actions");
        Check(buttons.Count(b => b.Visible) == 4, "Only four actions are shown on the main screen");
        Check(buttons.Any(b => b.Text == "Choose folders"), "Folder selection action is visible");
        Check(Field<ContextMenuStrip>("_restoreMenu").Items.Count == 2, "Restore menu preserves restore and move-from-device options");
        Check(Field<Button>("_backupActionButton").Text == "Back up now", "Idle state offers backup, not Stop");
        Check(Field<Button>("_agentActionButton").Text == "Disable agent", "Enabled agent offers only Disable");
        Check(buttons.Where(b => b.Visible).Select(b => b.Height).Distinct().Count() == 1, "Main action heights match");
        status.LastOperationStatus = "Uploading";
        Invoke("UpdateStatusLabel", status, config);
        Check(Field<Button>("_backupActionButton").Text == "Stop backup" && Field<Button>("_backupActionButton").Enabled,
            "Scheduled background upload changes the same button to Stop");
        Check(!Field<Button>("_agentActionButton").Enabled && !Field<Button>("_restoreButton").Enabled,
            "Agent and restore controls cannot interrupt active backup");
        typeof(MainForm).GetField("_stopRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
        Invoke("UpdateActionButtons");
        Check(Field<Button>("_backupActionButton").Text == "Stopping..." && !Field<Button>("_backupActionButton").Enabled, "Stop transition prevents duplicate requests");
        typeof(MainForm).GetField("_stopRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, false);
        status.LastOperationStatus = "Stopped";
        status.AgentEnabled = false;
        Invoke("UpdateStatusLabel", status, config);
        Check(Field<Button>("_backupActionButton").Text == "Back up now" && Field<Button>("_agentActionButton").Text == "Enable agent", "Stopped and disabled states restore the correct actions");
        typeof(MainForm).GetField("_pendingAction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, "Enabling");
        Invoke("UpdateActionButtons");
        Check(Field<Button>("_agentActionButton").Text == "Enabling..." && !Field<Button>("_agentActionButton").Enabled, "Enable transition is visible and cannot be repeated");
        typeof(MainForm).GetField("_pendingAction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, null);
        status.LastOperationStatus = "NoChanges";
        status.AgentEnabled = true;
        Invoke("UpdateStatusLabel", status, config);
        typeof(MainForm).GetField("_backupRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
        var canRun = typeof(MainForm).GetMethod("CanRunAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check((bool)canRun.Invoke(form, ["Stop backup"])!, "Stop action remains usable while a manual backup is active");
        Check(!(bool)canRun.Invoke(form, ["Back up now"])!, "Duplicate backup action remains blocked during active work");
        typeof(MainForm).GetField("_backupRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, false);
        typeof(MainForm).GetField("_upgrading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
        Check(!(bool)canRun.Invoke(form, ["Back up now"])! && !(bool)canRun.Invoke(form, ["Move data to this device"])!, "Backup and migration cannot race an agent update");
        typeof(MainForm).GetField("_upgrading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, false);
        Invoke("ShowProgress", "Uploading your files...");
        Check(Field<ProgressBar>("_progressBar").Visible, "Busy indicator shown");
        Invoke("ShowCompleted", "No new changes. Your backup is up to date.");
        Check(!Field<ProgressBar>("_progressBar").Visible, "Completed indicator stops");
        Invoke("ShowPending", "Request queued. The agent is uploading package 4.");
        Check(!Field<ProgressBar>("_progressBar").Visible && Field<Label>("_progressLabel").Text.Contains("queued"), "Queued request has a clear message without a looping progress bar");
        Check(Field<Label>("_progressLabel").ForeColor == Color.FromArgb(130, 90, 20), "Queued request uses a neutral warning color, not failure red");
        Invoke("ShowProgress", "Requested scan is now uploading...");
        Check(Field<ProgressBar>("_progressBar").Visible, "Progress resumes when queued scan starts");
        Invoke("ShowCompleted", "Backup completed successfully.");
        Check(!Field<ProgressBar>("_progressBar").Visible, "Matching completion clears active progress");
        SaveImage("client-desktop.png");
        form.ClientSize = new Size(750, 560);
        Application.DoEvents();
        var scroll = form.Controls.OfType<Panel>().Single();
        Check(!scroll.HorizontalScroll.Visible, "Small window has no horizontal scrolling");
        Field<TableLayoutPanel>("_detailsPanel").Visible = true;
        Application.DoEvents();
        Check(scroll.VerticalScroll.Visible, "Expanded details scroll in smaller windows");
        scroll.ScrollControlIntoView(Field<TextBox>("_serverUrlTextBox"));
        Check(Math.Abs(Field<Panel>("_serverInputFrame").Height - Field<Button>("_testButton").Height) <= 2, "Server input frame and test button have matching heights");
        scroll.AutoScrollPosition = new Point(0, scroll.VerticalScroll.Maximum);
        SaveImage("client-restore.png");
        Field<TableLayoutPanel>("_detailsPanel").Visible = false;
        form.ClientSize = new Size(880, 790);
        scroll.AutoScrollPosition = Point.Empty;
        config.CustomFolders = Enumerable.Range(1, 12).Select(i => $@"E:\Department-{i}\Shared reports and project documents").ToList();
        status.LastError = "Server unavailable. Queued backups will retry when the connection returns.";
        status.ServerReachable = false;
        Invoke("UpdateStatusLabel", status, config);
        Invoke("ShowError", "Could not connect. Check your server address and network connection.");
        Check(Field<Label>("_errorValue").Visible && !Field<ProgressBar>("_progressBar").Visible, "Error stays readable without loading indicator");
        Application.DoEvents();
        Check(!scroll.HorizontalScroll.Visible, "Long source paths wrap");
        SaveImage("client-error.png");
        object?[] dialogArguments = [config, true, null, null, null];
        using var folderDialog = (Form)typeof(MainForm).GetMethod("BuildFolderDialog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, dialogArguments)!;
        folderDialog.StartPosition = FormStartPosition.Manual;
        folderDialog.Location = new Point(-30000, -30000);
        folderDialog.Show();
        var useServer = (CheckBox)dialogArguments[2]!;
        var folderList = (CheckedListBox)dialogArguments[3]!;
        var paths = (TextBox)dialogArguments[4]!;
        Check(!useServer.Checked && folderList.Enabled && paths.Enabled, "Local override allows editing both user and custom folders");
        Check(folderList.Items.Count == Shared.Constants.LinkSafeDefaults.KnownFolders.Length, "All supported user and app-profile folders are offered");
        useServer.Checked = true;
        Check(!folderList.Enabled && !paths.Enabled, "Use server settings disables local override controls");
        useServer.Checked = false;
        Application.DoEvents();
        if (args.Length > 0)
        {
            using var preview = new Bitmap(folderDialog.Width, folderDialog.Height);
            folderDialog.DrawToBitmap(preview, new Rectangle(Point.Empty, folderDialog.Size));
            preview.Save(Path.Combine(args[0], "folder-selection.png"), ImageFormat.Png);
        }
        folderDialog.Close();
        form.Close();
        Console.WriteLine("All client presentation checks passed. No backup/restore actions were executed.");

        T Field<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        void Invoke(string name, params object[] values) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, values);
        void SaveImage(string name)
        {
            if (args.Length == 0) return;
            Directory.CreateDirectory(args[0]);
            Application.DoEvents();
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(args[0], name), ImageFormat.Png);
        }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
