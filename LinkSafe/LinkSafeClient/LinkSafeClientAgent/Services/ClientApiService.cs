using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class ClientApiService(IHttpClientFactory httpClientFactory)
{
    private sealed class BackupPollResponse { public RemoteBackupRequest? Request { get; set; } }
    public async Task<RemoteBackupRequest?> PollBackupRequestAsync(ClientAgentConfig config, RemoteBackupPoll report, CancellationToken token)
    {
        using var client = CreateClient(config);
        using var response = await client.PostAsJsonAsync("/api/client/backup-request/poll", report, token);
        // An older server simply has no remote-dispatch feature.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BackupPollResponse>(cancellationToken: token))?.Request;
    }
    private static readonly TimeSpan RestoreOptionsTimeout = TimeSpan.FromSeconds(5);

    public async Task<HealthResponse?> HealthAsync(string serverUrl, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        return await client.GetFromJsonAsync<HealthResponse>($"{serverUrl.TrimEnd('/')}/api/health", cancellationToken);
    }

    public async Task<RegistrationResponse?> RegisterAsync(ClientAgentConfig config, ClientIdentityInfo identity, CancellationToken cancellationToken, List<string>? availableDrives = null)
    {
        using var client = CreateClient(config);
        var request = new RegistrationRequest
        {
            DomainUser = identity.DomainUser,
            Username = identity.Username,
            Domain = identity.Domain,
            ComputerName = identity.ComputerName,
            AgentVersion = "1.0.16",
            AvailableDrives = availableDrives ?? FolderMappingService.GetAvailableDrives(),
            UsesLocalFolderSelection = config.UsesLocalFolderSelection,
            ReportedBackupFolders = config.BackupFolders,
            ReportedCustomFolders = config.CustomFolders
        };

        using var response = await client.PostAsJsonAsync("/api/client/register", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RegistrationResponse>(cancellationToken: cancellationToken);
    }

    public async Task<ClientPolicyResponse?> GetPolicyAsync(ClientAgentConfig config, CancellationToken cancellationToken)
    {
        var identity = new ClientIdentityInfo
        {
            DomainUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name,
            Username = Environment.UserName,
            Domain = Environment.UserDomainName,
            ComputerName = Environment.MachineName
        };

        using var client = CreateClient(config);
        var query = $"/api/config/client?domainUser={Uri.EscapeDataString(identity.DomainUser)}&computerName={Uri.EscapeDataString(identity.ComputerName)}";
        return await client.GetFromJsonAsync<ClientPolicyResponse>(query, cancellationToken);
    }

    public async Task<BackupUploadResponse?> UploadAsync(
        ClientAgentConfig config,
        string zipPath,
        string metadataJson,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(config);
        client.Timeout = TimeSpan.FromMinutes(30);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"), "metadata");

        var fileStream = File.OpenRead(zipPath);
        form.Add(new StreamContent(fileStream), "file", Path.GetFileName(zipPath));

        var response = await client.PostAsync("/api/backup/upload", form, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BackupUploadResponse>(cancellationToken: cancellationToken);
    }

    public async Task<Dictionary<string, BackupFileStateEntry>> VerifyBackupStateAsync(
        ClientAgentConfig config, ClientIdentityInfo identity,
        IReadOnlyDictionary<string, BackupFileStateEntry> state,
        Func<int, int, Task>? progress, CancellationToken cancellationToken)
    {
        using var client = CreateClient(config);
        client.Timeout = TimeSpan.FromMinutes(2);
        var verified = new Dictionary<string, BackupFileStateEntry>(StringComparer.OrdinalIgnoreCase);
        var files = state.Values.Where(f => !string.IsNullOrWhiteSpace(f.ArchiveEntryPath)).ToList();
        if (files.Any(f => f.IsDirectory))
        {
            // Older servers accept an empty check but cannot interpret directory records.
            using var capabilityResponse = await client.PostAsJsonAsync("/api/backup/check", new BackupPresenceRequest
            {
                DomainUser = identity.DomainUser, ComputerName = identity.ComputerName
            }, cancellationToken);
            capabilityResponse.EnsureSuccessStatusCode();
            var capability = await capabilityResponse.Content.ReadFromJsonAsync<BackupPresenceResponse>(cancellationToken: cancellationToken);
            if (capability?.SupportsDirectories != true)
                throw new InvalidOperationException("Update the LinkSafe server to 1.0.11 or later to verify backed-up folders.");
        }
        var checkedCount = 0;
        foreach (var batch in files.Chunk(BackupPresenceRequest.BatchSize))
        {
            using var response = await client.PostAsJsonAsync("/api/backup/check", new BackupPresenceRequest
            {
                DomainUser = identity.DomainUser,
                ComputerName = identity.ComputerName,
                Files = batch.Select(f => new BackupPresenceEntry
                {
                    RelativePath = f.ArchiveEntryPath.Replace('\\', '/'), Length = f.Length, IsDirectory = f.IsDirectory
                }).ToList()
            }, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new HttpRequestException("Update the LinkSafe server to 1.0.4 or later to verify missing backup files.", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<BackupPresenceResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Server returned no backup verification result.");
            if (result.PresentPaths is null) throw new InvalidDataException("Server returned an invalid backup verification result.");
            var present = result.PresentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in batch)
                if (present.Contains(file.ArchiveEntryPath.Replace('\\', '/'))) verified[file.SourcePath] = file;
            checkedCount += batch.Length;
            if (progress is not null) await progress(checkedCount, files.Count);
        }
        return verified;
    }

    public async Task SendHeartbeatAsync(ClientAgentConfig config, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        using var client = CreateClient(config);
        await client.PostAsJsonAsync("/api/client/heartbeat", request, cancellationToken);
    }

    public async Task<RoamingRestoreOptionsResponse?> GetRoamingRestoreOptionsAsync(
        ClientAgentConfig config,
        ClientIdentityInfo identity,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(config);
        client.Timeout = RestoreOptionsTimeout;
        var query =
            $"/api/restore/options?domainUser={Uri.EscapeDataString(identity.DomainUser)}&computerName={Uri.EscapeDataString(identity.ComputerName)}";
        return await client.GetFromJsonAsync<RoamingRestoreOptionsResponse>(query, cancellationToken);
    }

    public async Task DownloadRestoreArchiveAsync(
        ClientAgentConfig config,
        string domainUser,
        string sourceComputerName,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(config);
        var query =
            $"/api/restore/package?domainUser={Uri.EscapeDataString(domainUser)}&sourceComputerName={Uri.EscapeDataString(sourceComputerName)}";
        using var response = await client.GetAsync(query, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await responseStream.CopyToAsync(fileStream, cancellationToken);
    }

    public async Task StreamRestoreArchiveAsync(
        ClientAgentConfig config,
        string domainUser,
        string sourceComputerName,
        Func<Stream, CancellationToken, Task> streamHandler,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(config);
        var query =
            $"/api/restore/package?domainUser={Uri.EscapeDataString(domainUser)}&sourceComputerName={Uri.EscapeDataString(sourceComputerName)}";
        using var response = await client.GetAsync(query, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await streamHandler(responseStream, cancellationToken);
    }

    private HttpClient CreateClient(ClientAgentConfig config)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/'));
        return client;
    }

    public async Task<MissingRestorePlan> CompareRestoreAsync(ClientAgentConfig config, string user, string computer,
        IReadOnlyDictionary<string, string> roots, CancellationToken cancellationToken)
    {
        using var client = CreateClient(config);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return await MissingFileRestore.CompareAsync(client, user, computer, roots, cancellationToken, config.ProtectedSourcePaths);
    }

    public async Task<MissingRestoreResult> RestoreMissingAsync(ClientAgentConfig config, MissingRestorePlan plan,
        Func<string, int, Task>? progress, CancellationToken cancellationToken)
    {
        using var client = CreateClient(config);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return await MissingFileRestore.ApplyAsync(client, plan, progress, cancellationToken);
    }
}
