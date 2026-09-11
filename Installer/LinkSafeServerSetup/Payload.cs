using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LinkSafeServerSetup;

internal static class Payload
{
    internal static string Destination(string root, string entry)
    {
        var parts = entry.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Invalid installer payload path.");
        if (parts.Any(p => System.Text.RegularExpressions.Regex.IsMatch(p.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Reserved Windows name in installer payload.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(parts)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installer payload escapes its staging directory.");
        return path;
    }

    internal static Stream Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("Setup payload is missing. Build using Build-LinkSafeServerSetup.ps1.");

    internal static void Extract(string root)
    {
        using var resource = Resource("LinkSafe.Payload.zip");
        Extract(root, resource);
    }

    internal static void Extract(string root, Stream resource)
    {
        using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var destination = Destination(root, entry.FullName);
            if (!seen.Add(destination)) throw new InvalidDataException("Duplicate installer payload entry.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
        var manifestPath = Path.Combine(root, "manifest.json");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Missing payload manifest.");
        if (manifest.Count != seen.Count - 1) throw new InvalidDataException("Payload manifest file count differs.");
        foreach (var (relative, hash) in manifest)
        {
            var path = Destination(root, relative);
            if (!seen.Contains(path)) throw new InvalidDataException("Payload file is missing.");
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Payload verification failed: {relative}");
        }
    }
}
