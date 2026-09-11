namespace LinkSafeClientSetup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try { Shared.Storage.ClientWorkspace.EnsureCreated(); LegacyClientMigration.SeedServerAddress(); }
        catch (Exception ex)
        {
            MessageBox.Show("LinkSafe cannot prepare your private client folder: " + ex.Message +
                " Open this client under your normal Windows account. Do not grant Everyone access to backup data.", "LinkSafe Client");
            return;
        }
        var tray = args.Contains("--tray");
        var uninstall = args.Contains("--uninstall");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var name = $@"Local\LinkSafe.Client.{sid}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        using var mutex = new Mutex(true, name, out var first);
        using var open = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Open");
        using var remove = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Uninstall");
        if (!first)
        {
            using var installed = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ClientDesktopInstallation.UninstallKey);
            if (!uninstall && !tray && Version.TryParse(installed?.GetValue("DisplayVersion") as string, out var installedVersion) &&
                installedVersion < typeof(MainForm).Assembly.GetName().Version)
            {
                MessageBox.Show("An older LinkSafe client window is still running. Choose Exit from its tray menu, then run this new setup again. Backups continue in the background.", "LinkSafe Client update");
                return;
            }
            if (uninstall) remove.Set();
            else if (!tray) open.Set();
            return;
        }
        using var form = new MainForm(true, tray, uninstall);
        _ = form.Handle;
        void Dispatch(Action action)
        {
            try { if (!form.IsDisposed) form.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }
        var openWait = ThreadPool.RegisterWaitForSingleObject(open, (_, _) => Dispatch(form.OpenFromLauncher), null, -1, false);
        var removeWait = ThreadPool.RegisterWaitForSingleObject(remove, (_, _) => Dispatch(form.UninstallFromLauncher), null, -1, false);
        try { Application.Run(form); }
        finally { openWait.Unregister(null); removeWait.Unregister(null); mutex.ReleaseMutex(); }
    }
}
