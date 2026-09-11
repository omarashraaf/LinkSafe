using System.Text.RegularExpressions;

namespace Shared.Storage;

public static class BackupSourcePaths
{
    public static bool TryNormalize(string? value, out string path)
    {
        path = string.Empty;
        var input = value?.Trim().Replace('/', '\\') ?? string.Empty;
        if (!Regex.IsMatch(input, @"^[A-Za-z]:\\") || input.Any(char.IsControl) || input[2..].IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
            return false;
        var segments = input[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')))
            return false;
        path = char.ToUpperInvariant(input[0]) + @":\" + string.Join('\\', segments);
        return true;
    }

    public static string ArchiveRoot(string path)
    {
        if (!TryNormalize(path, out var normalized))
            throw new ArgumentException("Use a local absolute path, such as D:\\ or D:\\Projects.", nameof(path));
        return $"Drives/{normalized[0]}" + (normalized.Length > 3 ? "/" + normalized[3..].Replace('\\', '/') : "");
    }

    public static bool IsWithin(string path, string root) =>
        path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
