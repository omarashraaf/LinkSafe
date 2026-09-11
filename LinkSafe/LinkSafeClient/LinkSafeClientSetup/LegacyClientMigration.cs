using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientSetup;

public static class LegacyClientMigration
{
    public static void SeedServerAddress(string? legacyRoot = null, string? newRoot = null)
    {
        var destination = Path.Combine(newRoot ?? LinkSafeDefaults.ClientDataRoot, "client-agent-config.json");
        if (File.Exists(destination)) return;
        var source = Path.Combine(legacyRoot ?? LinkSafeDefaults.LegacyClientRoot, "client-agent-config.json");
        try
        {
            RestorePathSafety.AssertNoLinks(source);
            if (!File.Exists(source) || new FileInfo(source).Length > 65536) return;
            var old = JsonSerializer.Deserialize<ClientAgentConfig>(File.ReadAllText(source), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!Uri.TryCreate(old?.ServerUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return;
            // Shared legacy state has no trustworthy user ownership: copy only the server address.
            // Never adopt another account's identity, file history, selection, restore consent or queue.
            using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, new ClientAgentConfig { ServerUrl = uri.GetLeftPart(UriPartial.Authority) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public static bool IsOwnedLegacyTask(string xml, string sid, string executable, Func<string, string?>? resolveAccount = null)
    {
        var ns = XNamespace.Get("http://schemas.microsoft.com/windows/2004/02/mit/task");
        var task = XDocument.Parse(xml).Root ?? throw new InvalidDataException("Invalid legacy startup task.");
        var owner = task.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "UserId")?.Value;
        if (!AgentStartupTask.SameUser(owner, sid, resolveAccount)) return false;
        var actions = task.Element(ns + "Actions");
        var command = actions?.Element(ns + "Exec")?.Element(ns + "Command")?.Value.Trim('"');
        if (actions?.Elements().Count() != 1 || !string.Equals(command, executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An older LinkSafe task for this user points to another installation. Ask IT to remove that old task before enabling this agent.");
        return true;
    }

    public static object? FindLegacyTask(Func<object> lookup)
    {
        try { return lookup(); }
        // COM maps missing tasks to FileNotFoundException/DirectoryNotFoundException,
        // not necessarily COMException. Only a missing optional task is harmless.
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002) || ex.HResult == unchecked((int)0x80070003))
        {
            return null;
        }
    }

    public static void RetireOwnedTask(string sid)
    {
        dynamic? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            folder = service.GetFolder(@"\");
            task = FindLegacyTask(() => (object)folder.GetTask(AgentStartupTask.Name));
            if (task is null) return;
            if (IsOwnedLegacyTask((string)task.Xml, sid, Path.Combine(LinkSafeDefaults.LegacyClientRoot, "bin", "LinkSafeClientAgent.exe")))
                folder.DeleteTask(AgentStartupTask.Name, 0);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Windows blocked removal of an older startup task. IT must remove the old 'LinkSafe Client Agent' task for this user once. Do not grant Everyone permissions or run backups as another account.", ex);
        }
        finally
        {
            if (task is not null) Marshal.FinalReleaseComObject(task);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }

    public static bool ProcessBelongsToUser(Process process, string sid)
    {
        if (!OpenProcessToken(process.Handle, 0x0008, out var token))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value == sid;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    public static async Task RetireAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        RetireOwnedTask(sid);
        using var current = Process.GetCurrentProcess();
        var legacyExe = Path.Combine(LinkSafeDefaults.LegacyClientRoot, "bin", "LinkSafeClientAgent.exe");
        foreach (var process in Process.GetProcessesByName("LinkSafeClientAgent"))
        using (process)
        {
            try
            {
                if (process.HasExited || process.SessionId != current.SessionId) continue;
                if (!string.Equals(process.MainModule?.FileName, legacyExe, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ProcessBelongsToUser(process, sid)) continue;
                // Original files and queued ZIPs stay in the legacy workspace for recovery.
                process.Kill();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("An older agent in this session could not be inspected or stopped. Ask IT to close the old elevated agent once, then enable this client under your normal account. The old data is retained.", ex);
            }
        }
    }
}
