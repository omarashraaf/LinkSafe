using LinkSafeServerSetup;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var root = Path.Combine(Path.GetTempPath(), "LinkSafeSetupPathChecks");
        var checks = 0;
        foreach (var entry in new[] { "../outside.exe", "..\\outside.exe", "C:/Windows/file", "/rooted", "folder/../file", "folder/file:ads", "folder./file", "folder /file", "folder//file", "Server/CON.txt", "Server/lpt1" })
        {
            try { Payload.Destination(root, entry); throw new Exception($"Accepted unsafe payload path: {entry}"); }
            catch (InvalidDataException) { checks++; }
        }
        if (Payload.Destination(root, "Server/wwwroot/css/site.css") != Path.Combine(root, "Server", "wwwroot", "css", "site.css")) throw new Exception("Normal payload path rejected.");
        checks++;
        var fixtures = Path.GetFullPath(Path.Combine("artifacts", "server-setup-tests", Guid.NewGuid().ToString("N")));
        checks += ReplaceCompatibilityChecks.Run(fixtures);
        foreach (var scenario in new[] { "valid", "bad-hash", "duplicate", "unlisted", "traversal" })
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
                Add("Server/test.txt", "payload");
                if (scenario == "duplicate") Add("Server/test.txt", "payload");
                if (scenario == "unlisted") Add("Server/extra.txt", "payload");
                if (scenario == "traversal") Add("../escape.txt", "payload");
                var hash = scenario == "bad-hash" ? "INVALID" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payload")));
                Add("manifest.json", JsonSerializer.Serialize(new Dictionary<string, string> { ["Server/test.txt"] = hash }));
            }
            memory.Position = 0;
            var rejected = false;
            try { Payload.Extract(Path.Combine(fixtures, scenario), memory); }
            catch (InvalidDataException) { rejected = true; }
            if (rejected != (scenario != "valid")) throw new Exception($"Wrong payload verification result: {scenario}");
            checks++;
        }
        if (args.Length > 1 && args[1] == "--verify-payload")
        {
            var extracted = Path.Combine(fixtures, "real-payload");
            Payload.Extract(extracted);
            foreach (var file in new[] { "Server/LinkSafeServer.exe", "Server/coreclr.dll", "Server/wwwroot/css/site.css", "Server/wwwroot/js/live.js", "Server/Microsoft.Extensions.Hosting.WindowsServices.dll", "Downloads/LinkSafeClientSetup.exe", "Downloads/LinkSafeClientAgent.exe" })
                if (!File.Exists(Path.Combine(extracted, file))) throw new Exception($"Required package file missing: {file}");
            if (Directory.EnumerateFiles(extracted, "server-config.json", SearchOption.AllDirectories).Any()) throw new Exception("Runtime config unexpectedly packaged.");
            checks += 8;
        }
        using var form = new SetupForm { ShowInTaskbar = false, Opacity = 0 };
        form.Show();
        Application.DoEvents();
        var buttons = Descendants(form).OfType<Button>().ToArray();
        if (!buttons.Any(b => b.Text.Contains("Install") && b.Enabled)) throw new Exception("Install action missing.");
        if (!buttons.Any(b => b.Text.Contains("Enable LAN"))) throw new Exception("LAN action missing.");
        if (!Descendants(form).OfType<TextBox>().Any(t => t.AccessibleName == "Backup storage folder")) throw new Exception("Backup folder field missing.");
        checks += 3;
        foreach (var size in new[] { new Size(820, 730), new Size(724, 651) })
        {
            form.ClientSize = size;
            form.PerformLayout();
            Application.DoEvents();
            foreach (var button in buttons)
            {
                var relative = form.PointToClient(button.PointToScreen(Point.Empty));
                if (relative.X < 0 || relative.X + button.Width > form.ClientSize.Width || relative.Y + button.Height > form.ClientSize.Height)
                    throw new Exception($"Action clipped at {size}: {button.Text}");
            }
            checks++;
        }
        form.ClientSize = new Size(820, 730);
        form.PerformLayout();
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "setup-preview.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle with { Width = form.Width, Height = form.Height });
        bitmap.Save(output);
        Console.WriteLine($"PASS: {checks} setup path and UI checks. No services, firewall rules, config or backups changed.");
        Console.WriteLine($"Preview: {output}");
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
