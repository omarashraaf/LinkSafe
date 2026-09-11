using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinkSafeServer.Models;
using LinkSafeServer.Services;

internal static class BulkChecks
{
    internal static async Task<int> RunAsync(HttpClient http, ClientRegistryService registry, RemoteBackupService requests, string fixture)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS: bulk " + message); }
        var all = await registry.GetAllAsync();
        var selected = all.Take(2).Select(BulkClientSelection.Key).ToList();
        var tokenHtml = await http.GetStringAsync("/Clients");
        var csrf = WebUtility.HtmlDecode(Regex.Match(tokenHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        async Task<HttpResponseMessage> Post(string handler, string action, IEnumerable<string> keys, bool confirmed = true, bool token = true, string[]? folders = null, string? paths = null)
        {
            var fields = keys.Select(key => new KeyValuePair<string, string>("SelectedClients", key)).ToList();
            fields.Add(new("BulkAction", action));
            if (confirmed) fields.Add(new("confirmed", "true"));
            if (token) fields.Add(new("__RequestVerificationToken", csrf));
            if (folders is not null) fields.AddRange(folders.Select(f => new KeyValuePair<string, string>("SelectedFolders", f)));
            if (paths is not null) fields.Add(new("CustomFoldersText", paths));
            return await http.PostAsync("/Clients?handler=" + handler, new FormUrlEncodedContent(fields));
        }
        var initial = JsonSerializer.Serialize(await registry.GetAllAsync());
        using var anon = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = http.BaseAddress };
        var denied = await anon.PostAsync("/Clients?handler=Apply", new FormUrlEncodedContent(new Dictionary<string,string> { ["BulkAction"]="Remove", ["confirmed"]="true", ["SelectedClients"]=selected[0] }));
        Check(denied.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest, "anonymous mutation denied");
        Check((await Post("Apply", "Remove", selected, token:false)).StatusCode == HttpStatusCode.BadRequest, "missing CSRF denied");
        Check((await Post("Preview", "Remove", selected)).StatusCode == HttpStatusCode.OK, "removal review renders");
        Check(JsonSerializer.Serialize(await registry.GetAllAsync()) == initial, "preview never removes clients");
        var unconfirmed = await Post("Apply", "Remove", selected, confirmed:false);
        Check((await unconfirmed.Content.ReadAsStringAsync()).Contains("confirm the action") && JsonSerializer.Serialize(await registry.GetAllAsync()) == initial, "confirmation required without writes");
        foreach (var keys in new[] { new List<string>(), new List<string> { selected[0], "stale-key" }, Enumerable.Repeat(selected[0], 201).ToList() })
        {
            await Post("Apply", "Disable", keys);
            Check(JsonSerializer.Serialize(await registry.GetAllAsync()) == initial, "empty/stale/oversized selection has no partial writes");
        }
        await Post("Apply", "Unknown", selected);
        Check(JsonSerializer.Serialize(await registry.GetAllAsync()) == initial, "unknown action rejected");
        foreach (var invalid in new[] { "..\\escape", "\\\\server\\share", "D:\\Projects\\..\\outside" })
        {
            await Post("Apply", "Settings", selected, folders:["Documents"], paths:invalid);
            Check(JsonSerializer.Serialize(await registry.GetAllAsync()) == initial, "unsafe custom folder rejected: " + invalid);
        }
        await Post("Apply", "Settings", selected, folders:["InvalidFolder"]);
        Check(JsonSerializer.Serialize(await registry.GetAllAsync()) == initial, "unknown user folder rejected");
        await Post("Apply", "Settings", selected);
        Check(JsonSerializer.Serialize(await registry.GetAllAsync()) == initial, "empty policy rejected");
        var updated = await Post("Apply", "Settings", [.. selected, selected[0]], folders:["Documents", "Desktop"], paths:"D:\\Projects\nD:\\Projects");
        Check(updated.StatusCode == HttpStatusCode.Redirect, "valid settings saved with duplicate targets deduplicated");
        await http.GetStringAsync("/Clients?live=1");
        Check((await http.GetStringAsync("/Clients")).Contains("Updated backup settings for 2 computers"), "live polling preserves pending action notice");
        var after = await registry.GetAllAsync();
        Check(after.Take(2).All(c => c.HasBackupPolicyOverride && c.BackupFoldersOverride.SequenceEqual(new[] { "Documents", "Desktop" }) && c.CustomFoldersOverride.SequenceEqual(new[] { "D:\\Projects" })), "both selected clients received exact policy");
        Check(JsonSerializer.Serialize(after.Skip(2)) == JsonSerializer.Serialize(all.Skip(2)), "unselected records unchanged");
        Check(after[0].UsesLocalFolderSelection == true && after[0].ReportedBackupFolders.SequenceEqual(new[] { "Desktop" }), "server settings preserve the reported local PC override");
        await Post("Apply", "Defaults", selected);
        Check((await registry.GetAllAsync()).Take(2).All(c => !c.HasBackupPolicyOverride && c.BackupFoldersOverride.Count == 0 && c.CustomFoldersOverride.Count == 0), "defaults clear server overrides");
        await Post("Apply", "Disable", selected);
        Check((await registry.GetAllAsync()).Take(2).All(c => c.IsDisabled), "disable updates selected clients");
        await Post("Apply", "Enable", selected);
        Check((await registry.GetAllAsync()).Take(2).All(c => !c.IsDisabled), "enable updates selected clients");
        var beforeIds = (await requests.GetAllAsync(default)).Select(x => x.RequestId).ToList();
        Check((await Post("Apply", "Backup", all.Select(BulkClientSelection.Key))).StatusCode == HttpStatusCode.Redirect, "mixed backup selection accepted with per-client eligibility");
        var notice = WebUtility.HtmlDecode(await http.GetStringAsync("/Clients"));
        Check(notice.Contains("2 backup requests queued or already pending; 2 skipped"), "backup summary separates pending from skipped");
        Check((await requests.GetAllAsync(default)).Select(x => x.RequestId).SequenceEqual(beforeIds), "bulk backup does not duplicate pending requests");
        var beforeRemove = await registry.GetAllAsync();
        var backupFolder = Path.Combine(fixture, "retained-backups", "OFFLINE-PC");
        Directory.CreateDirectory(backupFolder);
        beforeRemove.Single(c => BulkClientSelection.Key(c) == selected[1]).BackupDestinationPath = backupFolder;
        await new Shared.Storage.JsonFileStore<List<Shared.Models.RegisteredClientRecord>>(Path.Combine(fixture, "clients.json")).SaveAsync(beforeRemove);
        var sentinel = Path.Combine(backupFolder, "retained-backup.txt");
        await File.WriteAllTextAsync(sentinel, "keep backup data");
        await Post("Apply", "Remove", [selected[1]]);
        var remaining = await registry.GetAllAsync();
        Check(remaining.Count == all.Count - 1 && remaining.All(c => BulkClientSelection.Key(c) != selected[1]), "remove deletes only selected registry entry");
        Check(await File.ReadAllTextAsync(sentinel) == "keep backup data", "backup sentinel retained");
        // Return the isolated fixture to its initial records for repeatable browser QA.
        await new Shared.Storage.JsonFileStore<List<Shared.Models.RegisteredClientRecord>>(Path.Combine(fixture, "clients.json")).SaveAsync(all);
        return checks;
    }
}
