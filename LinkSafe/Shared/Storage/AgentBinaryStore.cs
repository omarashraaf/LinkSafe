using System.Diagnostics;

namespace Shared.Storage;

public sealed class AgentBinaryStore(string installationDirectory, Func<string, Version?>? versionReader = null)
{
    public string ExecutablePath => Path.Combine(Path.GetFullPath(installationDirectory), "LinkSafeClientAgent.exe");
    public Version? VersionOf(string path) => (versionReader ?? ReadVersion)(path);

    private static Version? ReadVersion(string path)
    {
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(info.OriginalFilename, "LinkSafeClientAgent.dll", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(info.OriginalFilename, "LinkSafeClientAgent.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return Version.TryParse(info.FileVersion, out var version) ? version : null;
    }

    private void CheckOwnedPath(string path)
    {
        var root = Path.GetFullPath(installationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Agent updates may only modify files directly inside the managed installation folder.");
        RestorePathSafety.AssertNoLinks(path);
    }

    public async Task ReplaceAsync(string candidate, Version minimumVersion, Func<Task> activate, Func<Task> stopFailedActivation)
    {
        CheckOwnedPath(ExecutablePath);
        CheckOwnedPath(candidate);
        var version = VersionOf(candidate);
        if (version is null || version < minimumVersion || version < VersionOf(ExecutablePath))
            throw new InvalidOperationException("The downloaded file is not a suitable LinkSafe agent update.");
        var backup = Path.Combine(installationDirectory, $"LinkSafeClientAgent.{Guid.NewGuid():N}.previous.exe");
        CheckOwnedPath(backup);
        var hadPrevious = File.Exists(ExecutablePath);
        if (hadPrevious) File.Replace(candidate, ExecutablePath, backup);
        else File.Move(candidate, ExecutablePath);
        try { await activate(); }
        catch
        {
            await stopFailedActivation();
            if (hadPrevious) File.Move(backup, ExecutablePath, true);
            else File.Delete(ExecutablePath);
            throw;
        }
        if (hadPrevious) File.Delete(backup);
    }

    public List<string> CleanupOlderCopies()
    {
        CheckOwnedPath(ExecutablePath);
        var current = VersionOf(ExecutablePath);
        var removed = new List<string>();
        if (current is null) return removed;
        // Only rollback copies created by LinkSafe are eligible, never arbitrary EXEs or user data.
        foreach (var file in Directory.EnumerateFiles(installationDirectory, "LinkSafeClientAgent.*.previous.exe", SearchOption.TopDirectoryOnly))
        {
            CheckOwnedPath(file);
            var name = Path.GetFileName(file);
            var id = name["LinkSafeClientAgent.".Length..^".previous.exe".Length];
            if (!Guid.TryParseExact(id, "N", out _) || VersionOf(file) is not { } version || version >= current) continue;
            File.Delete(file);
            removed.Add(name);
        }
        return removed;
    }
}
