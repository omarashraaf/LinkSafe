using System.Net;
using System.Text;
using LinkSafeServer.Services;

internal static class LiveChecks
{
    internal static async Task<int> RunAsync(HttpClient http, ServerConfigService configs, ClientRegistryService clients, DashboardService dashboard, LogReaderService logs, string fixture)
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS: live " + label); }
        using var anon = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = http.BaseAddress };
        foreach (var path in new[] { "/Clients?live=1", "/?live=1", "/Logs?live=1" })
        {
            Check((await anon.GetAsync(path)).StatusCode == HttpStatusCode.Redirect, "anonymous read denied: " + path);
            using var response = await http.GetAsync(path);
            Check(response.IsSuccessStatusCode && response.Headers.GetValues("X-LinkSafe-Live").Single() == "1", "authenticated live response: " + path);
            Check(response.Headers.CacheControl?.NoStore == true, "private status is never cached: " + path);
            Check((await response.Content.ReadAsStringAsync()).Contains("data-live-region"), "live region present: " + path);
        }
        var original = await clients.GetAllAsync();
        var client = original[0];
        await clients.UpdateHeartbeatAsync(new() { DomainUser=client.DomainUser, ComputerName=client.ComputerName, LastBackupStatus="Stopped", LastError="fixture pause" });
        Check((await http.GetStringAsync("/Clients?live=1")).Contains("fixture pause") == false, "non-rendered private fields are not added to live payload");
        Check((await http.GetStringAsync("/Clients?live=1")).Contains("data-status=\"Stopped\""), "heartbeat status visible on next read");
        await File.WriteAllTextAsync(logs.LogFilePath, string.Concat(Enumerable.Range(0, 30000).Select(i => $"line {i}: fixture text\r\n")) + "FINAL-LINE\r\n", Encoding.UTF8);
        var lines = await logs.ReadRecentLinesAsync();
        Check(lines.Count == 200 && lines[^1] == "FINAL-LINE", "bounded log tail returns latest 200 lines");
        Check(!lines.Any(line => line.Contains("line 0:")), "old log history excluded");
        Check((await http.GetStringAsync("/Logs?live=1")).Contains("FINAL-LINE"), "live logs show newly appended content");
        await File.WriteAllTextAsync(logs.LogFilePath, "<script>fixture</script>");
        Check((await http.GetStringAsync("/Logs?live=1")).Contains("&lt;script&gt;fixture&lt;/script&gt;"), "log HTML is encoded");
        await File.WriteAllTextAsync(logs.LogFilePath, new string('x', 300000));
        Check((await logs.ReadRecentLinesAsync()).Single().Contains("preview limit"), "oversized log lines are bounded with notice");
        await File.WriteAllTextAsync(logs.LogFilePath, "");
        Check((await logs.ReadRecentLinesAsync()).Count == 0, "empty log handled");
        var summary = await dashboard.GetSummaryAsync();
        for (var attempt=0; summary.StorageUsageState == "Calculating..." && attempt<50; attempt++) { await Task.Delay(10); summary = await dashboard.GetSummaryAsync(); }
        Check(summary.StorageUsageState == "Ready", "storage measurement completes asynchronously");
        var root = Path.Combine(fixture, "new-storage");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "data.bin"), new byte[1234]);
        await configs.SaveAsync(new() { BackupRoot=root });
        summary = await dashboard.GetSummaryAsync();
        for (var attempt=0; summary.StorageUsageState == "Calculating..." && attempt<50; attempt++) { await Task.Delay(10); summary = await dashboard.GetSummaryAsync(); }
        Check(summary.StorageUsageState == "Ready" && summary.StorageUsageBytes == 1234, "changing storage root invalidates measurement");
        await File.WriteAllBytesAsync(Path.Combine(root, "more.bin"), new byte[20]);
        Check((await dashboard.GetSummaryAsync()).StorageUsageBytes == 1234, "frequent dashboard polls reuse storage measurement");
        await new Shared.Storage.JsonFileStore<List<Shared.Models.RegisteredClientRecord>>(Path.Combine(fixture, "clients.json")).SaveAsync(original);
        return count;
    }
}
