using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LinkSafeClientSetup;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var count = 0;
        void Check(bool condition, string text) { if (!condition) throw new Exception(text); count++; Console.WriteLine("PASS: " + text); }
        var id = Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(Path.Combine("artifacts", "client-install-tests", id, "User's app with spaces"));
        Directory.CreateDirectory(root);
        var keyPath = @"Software\LinkSafeFixtureTests\" + id;
        using var registry = Registry.CurrentUser.CreateSubKey(keyPath);
        try
        {
            var source = Path.Combine(root, "download.exe");
            File.WriteAllText(source, "isolated executable fixture");
            var menu = Path.Combine(root, "Start menu");
            var installation = new ClientDesktopInstallation(Path.Combine(root, "Installed"), menu, registry);
            installation.Install(source, "1.0.12", true);
            Check(File.ReadAllText(installation.Executable) == File.ReadAllText(source), "Client copied to a stable installed location");
            using (var app = registry.OpenSubKey(ClientDesktopInstallation.UninstallKey))
            {
                Check((string?)app!.GetValue("DisplayName") == "LinkSafe Client", "Installed apps display name registered");
                Check((string?)app.GetValue("DisplayVersion") == "1.0.12", "Installed version registered");
                Check((string?)app.GetValue("UninstallString") == $"\"{installation.Executable}\" --uninstall", "Quoted uninstall command points to installed executable");
            }
            Check(installation.HasTrayStartup(), "Per-user login entry launches tray mode");
            Check(installation.StartupCommand.EndsWith(" --tray") && !installation.StartupCommand.Contains("ClientAgent.exe"), "Tray startup does not launch a duplicate worker");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            try
            {
                foreach (var (name, args) in new[] { ("LinkSafe Client", ""), ("Uninstall LinkSafe Client", "--uninstall") })
                {
                    dynamic link = shell.CreateShortcut(Path.Combine(menu, "LinkSafe", name + ".lnk"));
                    try { Check((string)link.TargetPath == installation.Executable && (string)link.Arguments == args, "Real Start menu shortcut: " + name); }
                    finally { Marshal.FinalReleaseComObject(link); }
                }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            installation.SetTrayStartup(false);
            Check(!installation.HasTrayStartup(), "Disable removes tray login launch");
            Check(File.Exists(Path.Combine(menu, "LinkSafe", "LinkSafe Client.lnk")), "Disabled client can still be opened from Start");
            installation.Install(installation.Executable, "1.0.12", true);
            Check(installation.HasTrayStartup(), "Enable from installed executable repairs tray startup");
            using (var run = registry.CreateSubKey(ClientDesktopInstallation.RunKey)) run.SetValue(ClientDesktopInstallation.AppName, "different.exe");
            var rejected = false;
            try { installation.SetTrayStartup(false); } catch (IOException) { rejected = true; }
            Check(rejected, "Unrelated startup command is not deleted");
            using (var run = registry.CreateSubKey(ClientDesktopInstallation.RunKey)) run.SetValue(ClientDesktopInstallation.AppName, installation.StartupCommand);
            var preserved = Path.Combine(root, "config-and-queue.txt");
            File.WriteAllText(preserved, "keep");
            var script = installation.CleanupScript(int.MaxValue);
            Check(!script.Contains("-Recurse") && script.Contains("-LiteralPath") && script.Contains("WaitForExit"), "Cleanup is bounded to one executable after UI exit");
            installation.RemoveRegistration();
            using (var removed = registry.OpenSubKey(ClientDesktopInstallation.UninstallKey)) Check(removed is null, "Uninstall removes Installed apps entry");
            Check(!installation.HasTrayStartup(), "Uninstall removes tray startup");
            Check(!Directory.EnumerateFiles(Path.Combine(menu, "LinkSafe")).Any(), "Uninstall removes both Start shortcuts");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using (var process = Process.Start(start)!)
            { Check(process.WaitForExit(15000) && process.ExitCode == 0, "Windows PowerShell executes cleanup with spaces and apostrophes safely"); }
            Check(!File.Exists(installation.Executable) && File.ReadAllText(preserved) == "keep" && File.Exists(source), "Cleanup removes only installed binary, preserving data and original download");
            using var form = (MainForm)Activator.CreateInstance(typeof(MainForm), BindingFlags.Instance | BindingFlags.NonPublic, null, [false], null)!;
            var close = typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
            var userClose = new FormClosingEventArgs(CloseReason.UserClosing, false);
            close.Invoke(form, [form, userClose]);
            Check(userClose.Cancel, "Closing the dashboard keeps the tray application alive");
            foreach (var reason in new[] { CloseReason.WindowsShutDown, CloseReason.ApplicationExitCall, CloseReason.TaskManagerClosing })
            {
                var args = new FormClosingEventArgs(reason, false);
                close.Invoke(form, [form, args]);
                Check(!args.Cancel, "Client permits shutdown reason: " + reason);
            }
            using var trayForm = (MainForm)Activator.CreateInstance(typeof(MainForm), BindingFlags.Instance | BindingFlags.NonPublic, null, [false, true, false], null)!;
            Check(trayForm.WindowState == FormWindowState.Minimized && !trayForm.ShowInTaskbar,
                "Login tray mode does not display a dashboard or taskbar window");
            Console.WriteLine($"All {count} client installation checks passed. Only isolated fixture files and a temporary registry subtree were used.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
    }
}
