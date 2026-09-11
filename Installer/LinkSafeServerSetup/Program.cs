namespace LinkSafeServerSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "LinkSafe Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new SetupForm());
    }
}
