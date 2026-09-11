using System.Net;
using System.Net.Http.Json;
using Shared.Models;
using Shared.Constants;

namespace Shared.Storage;

public sealed record MissingRestoreFile(RestoreFileEntry File, string Destination);
public sealed class MissingRestorePlan
{
    public string DomainUser { get; init; } = "";
    public string SourceComputer { get; init; } = "";
    public List<MissingRestoreFile> Missing { get; } = [];
    public int Existing { get; set; }
    public List<string> Warnings { get; } = [];
}
public sealed class MissingRestoreResult
{
    public int Restored { get; set; }
    public int Preserved { get; set; }
    public List<string> Errors { get; } = [];
    public string Summary => $"Restored {Restored:N0} missing files. Preserved {Preserved:N0} existing files. {Errors.Count:N0} warnings/errors.";
}

public static class MissingFileRestore
{
    public static IReadOnlyDictionary<string, string> BuildRoots(ClientAgentConfig config, Func<string, string> resolveKnownFolder)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in LinkSafeDefaults.KnownFolders)
        {
            var path = resolveKnownFolder(name);
            if (!string.IsNullOrWhiteSpace(path)) roots[name] = path;
        }
        foreach (var source in config.CustomFolders)
            if (BackupSourcePaths.TryNormalize(source, out var path) && Directory.Exists(Path.GetPathRoot(path)))
                roots[BackupSourcePaths.ArchiveRoot(path)] = path;
        return roots;
    }
    private static string Query(string user, string computer) =>
        $"domainUser={Uri.EscapeDataString(user)}&sourceComputerName={Uri.EscapeDataString(computer)}";

    public static async Task<MissingRestorePlan> CompareAsync(HttpClient client, string user, string computer,
        IReadOnlyDictionary<string, string> roots, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? protectedRoots = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await client.GetAsync("api/restore/manifest?" + Query(user, computer), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("The restore source is unavailable, or the server needs the 1.0.3 restore update.");
        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync<RestoreManifest>(cancellationToken: timeout.Token)
            ?? throw new InvalidDataException("The server returned an empty restore manifest.");
        return Compare(manifest, user, computer, roots, protectedRoots);
    }

    public static MissingRestorePlan Compare(RestoreManifest manifest, string user, string computer, IReadOnlyDictionary<string, string> roots,
        IReadOnlyCollection<string>? protectedRoots = null)
    {
        var plan = new MissingRestorePlan { DomainUser = user, SourceComputer = computer };
        plan.Warnings.AddRange(manifest.Warnings);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = (protectedRoots ?? []).Concat(new[]
        {
            Shared.Constants.LinkSafeDefaults.ClientDataRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LinkSafeClient"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LinkSafeAgent"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LinkSafeServer")
        }).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        foreach (var file in manifest.Files.OrderBy(f => f.RelativePath.StartsWith("Drives/", StringComparison.OrdinalIgnoreCase)).ThenByDescending(f => f.LastWriteTimeUtc))
        {
            var relative = RestorePathSafety.NormalizeRelative(file.RelativePath);
            if (file.Length < 0) throw new InvalidDataException("Invalid restore file size.");
            var root = roots.OrderByDescending(p => p.Key.Length)
                .FirstOrDefault(p => relative.StartsWith(p.Key + "/", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(root.Value))
            {
                var warning = "No enabled local destination for " + relative.Split('/')[0] + ".";
                if (!plan.Warnings.Contains(warning)) plan.Warnings.Add(warning);
                continue;
            }
            try
            {
                var destination = RestorePathSafety.UnderRoot(root.Value, relative[(root.Key.Length + 1)..]);
                if (blocked.Any(p => BackupSourcePaths.IsWithin(destination, Path.GetFullPath(p))))
                    throw new IOException("Protected system or LinkSafe storage destination is not restored automatically.");
                if (!seen.Add(destination)) continue;
                if (Directory.Exists(destination)) plan.Warnings.Add(relative + ": a directory occupies the destination file path.");
                else if (RestorePathSafety.Exists(destination)) plan.Existing++;
                else plan.Missing.Add(new MissingRestoreFile(file, destination));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                plan.Warnings.Add($"{relative}: {ex.Message}");
            }
        }
        return plan;
    }

    public static async Task<MissingRestoreResult> ApplyAsync(HttpClient client, MissingRestorePlan plan,
        Func<string, int, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new MissingRestoreResult { Preserved = plan.Existing };
        result.Errors.AddRange(plan.Warnings);
        var processed = 0;
        foreach (var item in plan.Missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? temporaryPath = null;
            try
            {
                RestorePathSafety.AssertNoLinks(item.Destination);
                if (RestorePathSafety.Exists(item.Destination)) { result.Preserved++; continue; }
                if (progress is not null) await progress($"Restoring {processed + 1:N0} of {plan.Missing.Count:N0}: {item.File.RelativePath}", processed * 100 / Math.Max(1, plan.Missing.Count));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(30));
                var query = "api/restore/file?" + Query(plan.DomainUser, plan.SourceComputer) +
                    $"&relativePath={Uri.EscapeDataString(item.File.RelativePath)}&length={item.File.Length}&lastWriteTicks={item.File.LastWriteTimeUtc.Ticks}";
                using var response = await client.GetAsync(query, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != item.File.Length)
                    throw new IOException("Server file changed since comparison; run restore again.");
                var directory = Path.GetDirectoryName(item.Destination)!;
                Directory.CreateDirectory(directory);
                RestorePathSafety.AssertNoLinks(item.Destination);
                temporaryPath = Path.Combine(directory, ".linksafe-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
                await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long copied = 0;
                    var nextProgress = DateTime.UtcNow;
                    int count;
                    while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        copied += count;
                        if (copied > item.File.Length) throw new IOException("Restore download exceeded its expected size.");
                        await target.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                        if (progress is not null && DateTime.UtcNow >= nextProgress)
                        {
                            var percent = (int)((processed + (double)copied / Math.Max(1, item.File.Length)) * 100 / Math.Max(1, plan.Missing.Count));
                            await progress($"Restoring {processed + 1:N0}/{plan.Missing.Count:N0}: {item.File.RelativePath} ({copied:N0}/{item.File.Length:N0} bytes)", Math.Clamp(percent, 0, 100));
                            nextProgress = DateTime.UtcNow.AddMilliseconds(500);
                        }
                    }
                    if (copied != item.File.Length) throw new IOException("Incomplete restore download; original destination was not changed.");
                }
                File.SetLastWriteTimeUtc(temporaryPath, item.File.LastWriteTimeUtc);
                RestorePathSafety.AssertNoLinks(item.Destination);
                try { File.Move(temporaryPath, item.Destination, overwrite: false); result.Restored++; }
                catch (IOException) when (RestorePathSafety.Exists(item.Destination)) { result.Preserved++; }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or ArgumentException)
            {
                result.Errors.Add($"{item.File.RelativePath}: {ex.Message}");
            }
            finally
            {
                processed++;
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Errors.Add("Temporary file cleanup: " + ex.Message); }
                }
            }
        }
        if (progress is not null) await progress(result.Summary, 100);
        return result;
    }
}
