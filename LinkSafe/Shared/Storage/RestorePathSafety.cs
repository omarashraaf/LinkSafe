using System.Text.RegularExpressions;

namespace Shared.Storage;

public static class RestorePathSafety
{
    public static string NormalizeRelative(string entry)
    {
        var parts = entry.Replace('\\', '/').Split('/');
        if (parts.Length < 2 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." ||
            p.EndsWith('.') || p.EndsWith(' ') || p.Any(char.IsControl) || p.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            Regex.IsMatch(p.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe restore path: " + entry);
        return string.Join('/', parts);
    }

    public static string UnderRoot(string root, string relative)
    {
        var normalized = NormalizeRelative("root/" + relative)[5..];
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Restore path escapes its source root.");
        AssertNoLinks(destination);
        return destination;
    }

    public static void AssertNoLinks(string path)
    {
        var cursor = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Restore will not follow a link or junction: " + cursor);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    public static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
