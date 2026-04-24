using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shared.Models;

namespace LinkSafeClientAgent.Services;

public sealed class ClientApiService(IHttpClientFactory httpClientFactory)
{
    public async Task<HealthResponse?> HealthAsync(string serverUrl, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        return await client.GetFromJsonAsync<HealthResponse>($"{serverUrl.TrimEnd('/')}/api/health", cancellationToken);
    }

    public async Task<RegistrationResponse?> RegisterAsync(ClientAgentConfig config, ClientIdentityInfo identity, CancellationToken cancellationToken)
    {
        using var client = CreateClient(config);
        var request = new RegistrationRequest
        {
            DomainUser = identity.DomainUser,
            Username = identity.Username,
            Domain = identity.Domain,
            ComputerName = identity.ComputerName,
            AgentVersion = "1.0.0"
        };

        var response = await client.PostAsJsonAsync("/api/client/register", request, cancellationToken);
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

    private HttpClient CreateClient(ClientAgentConfig config)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/'));
        return client;
    }
}
