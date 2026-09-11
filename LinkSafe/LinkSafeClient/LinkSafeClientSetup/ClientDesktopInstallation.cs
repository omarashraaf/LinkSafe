using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Shared.Storage;

namespace LinkSafeClientSetup;

public sealed class ClientDesktopInstallation(string? installRoot = null, string? programsRoot = null,
    RegistryKey? registryRoot = null, Action<string, string, string>? createShortcut = null)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LinkSafeClient";
    public const string AppName = "LinkSafe Client";
    public string Root { get; } = Path.GetFullPath(installRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LinkSafeClient"));
    private string Menu => Path.Combine(programsRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LinkSafe");
    private RegistryKey RegistryRoot => registryRoot ?? Registry.CurrentUser;
    public string Executable => Path.Combine(Root, "LinkSafeClientSetup.exe");
    public string StartupCommand => $"\"{Executable}\" --tray";

    public bool IsRegistered()
    {
        using var key = RegistryRoot.OpenSubKey(UninstallKey);
        return string.Equals(key?.GetValue("InstallLocation") as string, Root, StringComparison.OrdinalIgnoreCase);
    }

    public void Install(string source, string version, bool enableTray)
    {
        RestorePathSafety.AssertNoLinks(Root);
        RestorePathSafety.AssertNoLinks(Executable);
        using (var previous = RegistryRoot.OpenSubKey(UninstallKey))
        {
            if (previous is not null && !string.Equals(previous.GetValue("InstallLocation") as string, Root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A different LinkSafe client installation is registered. Uninstall it first.");
            if (File.Exists(Executable) && previous is null && !string.Equals(Path.GetFullPath(source), Executable, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The client installation folder contains an unregistered executable. It was not overwritten.");
        }
        Directory.CreateDirectory(Root);
        if (!string.Equals(Path.GetFullPath(source), Executable, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, temporary);
                File.Move(temporary, Executable, overwrite: true);
            }
            catch (IOException ex) { throw new IOException("Close any older installed LinkSafe client window using its tray Exit option, then try Enable agent again.", ex); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        using (var key = RegistryRoot.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "LinkSafe");
            key.SetValue("InstallLocation", Root);
            key.SetValue("DisplayIcon", Executable);
            key.SetValue("UninstallString", $"\"{Executable}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        RestorePathSafety.AssertNoLinks(Menu);
        Directory.CreateDirectory(Menu);
        foreach (var (name, arguments) in new[] { (AppName, ""), ("Uninstall LinkSafe Client", "--uninstall") })
        {
            var path = Path.Combine(Menu, name + ".lnk");
            RestorePathSafety.AssertNoLinks(path);
            (createShortcut ?? WriteShortcut)(path, Executable, arguments);
        }
        SetTrayStartup(enableTray);
    }

    public bool HasTrayStartup()
    {
        using var run = RegistryRoot.OpenSubKey(RunKey);
        return File.Exists(Executable) && string.Equals(run?.GetValue(AppName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
    }

    public void SetTrayStartup(bool enabled)
    {
        using var run = RegistryRoot.CreateSubKey(RunKey);
        var existing = run.GetValue(AppName) as string;
        if (existing is not null && !string.Equals(existing, StartupCommand, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A different LinkSafe tray startup command exists. It was not replaced.");
        if (enabled) run.SetValue(AppName, StartupCommand);
        else run.DeleteValue(AppName, throwOnMissingValue: false);
    }

    public void AssertRegistered()
    {
        using (var key = RegistryRoot.OpenSubKey(UninstallKey))
        {
            if (key is null || !string.Equals(key.GetValue("InstallLocation") as string, Root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("This user's client installation could not be verified. Nothing was removed.");
        }
    }

    public void RemoveRegistration()
    {
        AssertRegistered();
        SetTrayStartup(false);
        foreach (var name in new[] { AppName, "Uninstall LinkSafe Client" })
        {
            var path = Path.Combine(Menu, name + ".lnk");
            RestorePathSafety.AssertNoLinks(path);
            File.Delete(path);
        }
        RegistryRoot.DeleteSubKey(UninstallKey, throwOnMissingSubKey: false);
    }

    public string CleanupScript(int processId)
    {
        RestorePathSafety.AssertNoLinks(Executable);
        var literal = Executable.Replace("'", "''");
        // Delete only our single installed executable after its owning UI exits. Never recurse into user data.
        return $"$ErrorActionPreference='Stop'; $p=Get-Process -Id {processId} -ErrorAction SilentlyContinue; " +
            $"if ($p) {{ if (-not $p.WaitForExit(60000)) {{ exit 1 }} }}; " +
            $"$f='{literal}'; if (Test-Path -LiteralPath $f) {{ " +
            "if ((Get-Item -LiteralPath $f).Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 1 }; " +
            "$item=Get-Item -LiteralPath (Split-Path -Parent $f); while ($item) { " +
            "if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 1 }; $item=$item.Parent }; " +
            "Remove-Item -LiteralPath $f -Force }";
    }

    public void ScheduleExecutableCleanup()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(CleanupScript(Environment.ProcessId))));
        using var process = Process.Start(start) ?? throw new IOException("Could not start client cleanup.");
    }

    private static void WriteShortcut(string path, string target, string arguments)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic? shortcut = null;
        try
        {
            shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = target;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.IconLocation = target + ",0";
            shortcut.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
