namespace Shared.Models;

public sealed class HealthResponse
{
    public string Status { get; set; } = "ok";
    public string ServerName { get; set; } = Environment.MachineName;
    public string Version { get; set; } = "1.0.0";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
}
