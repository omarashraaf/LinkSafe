using Microsoft.AspNetCore.StaticFiles;

namespace LinkSafeServer.Services;

public sealed class DownloadCatalogService(ServerConfigService serverConfigService)
{
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();
    private const string TemplateSetupName = "LinkSafeClientSetup.exe";
    private const string TemplateAgentName = "LinkSafeClientAgent.exe";
    private const string GeneratedSetupPrefix = "LinkSafeClientSetup-";
    private const string GeneratedSetupSuffix = ".exe";

    public string DownloadsRoot => @"C:\ProgramData\LinkSafeServer\Downloads";

    public IReadOnlyList<DownloadItem> GetItems()
    {
        Directory.CreateDirectory(DownloadsRoot);

        return Directory.EnumerateFiles(DownloadsRoot)
            .Select(path => new FileInfo(path))
            .Where(file => IsGeneratedSetupName(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new DownloadItem(
                file.Name,
                file.FullName,
                GetContentType(file.Name),
                file.Length,
                file.LastWriteTimeUtc))
            .ToList();
    }

    public DownloadItem? GetItem(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        var fullPath = Path.Combine(DownloadsRoot, safeName);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var file = new FileInfo(fullPath);
        return new DownloadItem(
            file.Name,
            file.FullName,
            GetContentType(file.Name),
            file.Length,
            file.LastWriteTimeUtc);
    }

    public async Task<DownloadItem?> CreateCustomizedSetupAsync(string? serverUrl, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DownloadsRoot);

        var templateExe = Path.Combine(DownloadsRoot, TemplateSetupName);
        if (!File.Exists(templateExe))
        {
            return null;
        }

        var normalizedServerUrl = await NormalizeServerUrlAsync(serverUrl, cancellationToken);
        var outputName = BuildGeneratedSetupName(normalizedServerUrl);
        CleanupGeneratedFiles();
        var outputPath = Path.Combine(DownloadsRoot, outputName);

        File.Copy(templateExe, outputPath, true);

        return GetItem(outputName);
    }

    public DownloadItem? GetClientAgent() => GetItem(TemplateAgentName);

    public void DeleteGeneratedSetup()
    {
        foreach (var file in Directory.EnumerateFiles(DownloadsRoot))
        {
            if (IsGeneratedSetupName(Path.GetFileName(file)))
            {
                TryDeleteFile(file);
            }
        }
    }

    private async Task<string> NormalizeServerUrlAsync(string? serverUrl, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            serverUrl = serverUrl.Trim();
            var port = await GetServerPortAsync(cancellationToken);
            return $"http://{serverUrl}:{port}";
        }

        var config = await serverConfigService.GetAsync(cancellationToken);
        return config.ServerUrl.TrimEnd('/');
    }

    private async Task<int> GetServerPortAsync(CancellationToken cancellationToken)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        if (Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var uri))
        {
            return uri.Port;
        }

        return 5055;
    }

    private void CleanupGeneratedFiles()
    {
        foreach (var file in Directory.EnumerateFiles(DownloadsRoot))
        {
            var name = Path.GetFileName(file);
            var isTemplate =
                name.Equals(TemplateSetupName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(TemplateAgentName, StringComparison.OrdinalIgnoreCase);

            if (!isTemplate)
            {
                TryDeleteFile(file);
            }
        }
    }

    private static bool IsGeneratedSetupName(string fileName) =>
        fileName.StartsWith(GeneratedSetupPrefix, StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(GeneratedSetupSuffix, StringComparison.OrdinalIgnoreCase);

    private static string BuildGeneratedSetupName(string normalizedServerUrl)
    {
        if (Uri.TryCreate(normalizedServerUrl, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.Replace(':', '-');
            return $"{GeneratedSetupPrefix}{host}-p{uri.Port}{GeneratedSetupSuffix}";
        }

        return $"{GeneratedSetupPrefix}Generated-p5055{GeneratedSetupSuffix}";
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string GetContentType(string fileName) =>
        _contentTypeProvider.TryGetContentType(fileName, out var contentType)
            ? contentType
            : "application/octet-stream";
}

public sealed record DownloadItem(
    string Name,
    string FullPath,
    string ContentType,
    long SizeBytes,
    DateTimeOffset LastModified);
