using System.Text.Json;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeServer.Models;

public static class BulkClientSelection
{
    public const int Limit = 200;
    public static readonly string[] Actions = ["Backup", "Settings", "Defaults", "Enable", "Disable", "Remove"];

    public static string Key(RegisteredClientRecord client) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new[] { client.DomainUser.ToUpperInvariant(), client.ComputerName.ToUpperInvariant() }));

    public static List<RegisteredClientRecord> Resolve(IEnumerable<RegisteredClientRecord> clients, List<string> keys)
    {
        if (keys.Count is < 1 or > Limit) throw new ArgumentException($"Select between 1 and {Limit} computers.");
        var selected = keys.ToHashSet(StringComparer.Ordinal);
        var result = clients.Where(c => selected.Contains(Key(c))).ToList();
        if (result.Count != selected.Count) throw new ArgumentException("Some selected computers are no longer registered. Refresh the list and select again; nothing was changed.");
        return result;
    }

    public static (List<string> Folders, List<string> Custom) ValidatePolicy(List<string> folders, List<string> paths)
    {
        if (folders.Any(f => !LinkSafeDefaults.KnownFolders.Contains(f))) throw new ArgumentException("Unknown user folder selected.");
        if (paths.Count > 100) throw new ArgumentException("Use no more than 100 additional paths.");
        var normalized = new List<string>();
        foreach (var path in paths)
        {
            if (!BackupSourcePaths.TryNormalize(path, out var value)) throw new ArgumentException($"Invalid local path: {path}. Use a drive or folder such as D:\\ or D:\\Projects, without '..' or wildcards.");
            normalized.Add(value);
        }
        if (folders.Count + normalized.Count == 0) throw new ArgumentException("Select at least one backup folder or additional path.");
        return (folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
