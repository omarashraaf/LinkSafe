using System.IO.Compression;
using LinkSafeClientAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Models;

var checks = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS: " + message);
}
var request = Guid.NewGuid().ToString("D");
var status = new StatusFileModel { LastOperationStatus = "Success", LastOperationFinishedAt = DateTimeOffset.UtcNow };
Check(!BackupRequestProgress.IsComplete(status, request), "Old success cannot complete a new request");
status.CompletedBackupRequestId = Guid.NewGuid().ToString("D");
status.CompletedBackupRequestStatus = "Success";
Check(!BackupRequestProgress.IsComplete(status, request), "Another request's success is ignored");
status.CompletedBackupRequestId = request;
status.CompletedBackupRequestStatus = "Uploading";
Check(!BackupRequestProgress.IsComplete(status, request), "Active upload is not complete");
foreach (var result in new[] { "Success", "NoChanges", "Failed", "Queued" })
{
    status.CompletedBackupRequestStatus = result;
    Check(BackupRequestProgress.IsComplete(status, request), "Matching terminal state: " + result);
}
Check(!BackupRequestProgress.IsComplete(status, ""), "Empty request is never completed");

var busy = new StatusFileModel { LastOperationStatus = "Uploading", LastOperationMessage = "Uploading scheduled backup package 4 to the server...",
    LastOperationStartedAt = DateTimeOffset.UtcNow.AddMinutes(-20) };
Check(BackupRequestProgress.IsWaitingForAgent(busy, request, request), "Long scheduled upload keeps the manual request queued beyond three minutes");
Check(!BackupRequestProgress.IsComplete(busy, request), "Another upload cannot complete the queued manual request");
Check(BackupRequestProgress.WaitingMessage(busy).Contains("package 4"), "Waiting message includes current background work");
Check(!BackupRequestProgress.IsWaitingForAgent(busy, request, ""), "Missing signal is distinguishable from a queued request");
Check(!BackupRequestProgress.IsWaitingForAgent(busy, request, Guid.NewGuid().ToString("D")), "Different queued request is not mistaken for this request");
busy.LastOperationRequestId = request;
Check(!BackupRequestProgress.IsWaitingForAgent(busy, request, request), "Acknowledged request transitions out of waiting");
busy.CompletedBackupRequestId = request;
busy.CompletedBackupRequestStatus = "Success";
Check(BackupRequestProgress.IsComplete(busy, request), "Matching completion ends monitoring after the queued scan runs");

var fixture = Path.GetFullPath(Path.Combine("artifacts", "backup-flow-tests", Guid.NewGuid().ToString("N")));
var documents = Path.Combine(fixture, "profile", "Documents");
var desktop = Path.Combine(fixture, "profile", "Desktop");
var additional = Path.Combine(fixture, "additional-source");
foreach (var folder in new[] { documents, desktop, additional }) Directory.CreateDirectory(folder);
for (var i = 0; i < 10; i++) await File.WriteAllTextAsync(Path.Combine(documents, $"document-{i}.txt"), "document " + i);
await File.WriteAllTextAsync(Path.Combine(desktop, "desktop.txt"), "desktop data");
await File.WriteAllTextAsync(Path.Combine(additional, "drive.txt"), "additional data");
var mapping = new FolderMappingService(name => name switch { "Documents" => documents, "Desktop" => desktop, _ => "" });
var builder = new BackupPackageBuilder(mapping, new ClientIdentityService(), NullLogger<BackupPackageBuilder>.Instance, Path.Combine(fixture, "queue"));
var config = new ClientAgentConfig { BackupFolders = ["Documents", "Desktop"], CustomFolders = [additional] };
var messages = new List<string>();
Task Progress(string text, int? percent) { messages.Add(text); if (percent is < 0 or > 100) throw new Exception("Invalid progress percentage"); return Task.CompletedTask; }
var package = await builder.CreateSmartBackupAsync(config, null, new Dictionary<string, BackupFileStateEntry>(), [], CancellationToken.None, Progress)
    ?? throw new Exception("Expected initial package");
using (var zip = ZipFile.OpenRead(package.FilePath))
{
    Check(zip.Entries.Count(e => e.FullName.StartsWith("Documents/") && e.Name.Length > 0) == 10, "All ten Documents files packaged alongside additional source");
    Check(zip.GetEntry("Desktop/desktop.txt") != null, "Desktop file packaged alongside additional source");
    Check(zip.Entries.Any(e => e.FullName.EndsWith("/drive.txt")), "Additional-source file is retained");
}
Check(messages.Any(m => m.StartsWith("Scanning")), "Scanning progress reported");
Check(messages.Any(m => m.StartsWith("Packaging file")), "Packaging counts reported");
Check(package.IncludedFiles.All(f => !string.IsNullOrWhiteSpace(f.ArchiveEntryPath)), "Confirmed manifest includes archive destination");
var state = package.IncludedFiles.ToDictionary(f => f.SourcePath, StringComparer.OrdinalIgnoreCase);
var unchanged = await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, state, [], CancellationToken.None, Progress);
Check(unchanged == null, "Unchanged files with confirmed destinations are skipped");
foreach (var entry in state.Values.Where(f => f.SourcePath.StartsWith(documents) || f.SourcePath.StartsWith(desktop)))
    entry.ArchiveEntryPath = "Drives/C/previous-location/" + Path.GetFileName(entry.SourcePath);
var repaired = await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, state, [], CancellationToken.None, Progress)
    ?? throw new Exception("Remapped user folders incorrectly skipped");
Check(repaired.IncludedFiles.Count == 11, "User folders previously saved under a drive are repackaged");
using (var zip = ZipFile.OpenRead(repaired.FilePath))
    Check(zip.GetEntry("Documents/document-9.txt") != null && zip.GetEntry("Desktop/desktop.txt") != null, "Repair ZIP has original user-folder layout");
foreach (var file in repaired.IncludedFiles) state[file.SourcePath] = file;
await File.WriteAllTextAsync(Path.Combine(documents, "new.txt"), "new local document");
var increment = await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, state, [], CancellationToken.None, Progress);
Check(increment?.IncludedFiles.Count == 1 && increment.IncludedFiles[0].ArchiveEntryPath == "Documents/new.txt", "New local user-folder file detected after confirmed backup");
foreach (var entry in state.Values) entry.ArchiveEntryPath = "";
var legacy = await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, state, [], CancellationToken.None, Progress);
Check(legacy?.IncludedFiles.Count == 13, "Legacy history revalidates all selected files without deleting backup data");
using (var stopScan = new CancellationTokenSource())
{
    var allConfirmed = legacy!.IncludedFiles.ToDictionary(f => f.SourcePath, StringComparer.OrdinalIgnoreCase);
    var cancelledScan = false;
    try
    {
        await builder.CreateSmartBackupAsync(config, DateTimeOffset.UtcNow, allConfirmed, [], stopScan.Token,
            (_, _) => { stopScan.Cancel(); return Task.CompletedTask; });
    }
    catch (OperationCanceledException) { cancelledScan = true; }
    Check(cancelledScan, "Stop interrupts a scan even when all files are unchanged");
}
Console.WriteLine($"All {checks} isolated backup-flow checks passed. No live queue, server or user folders were used.");
