using Shared.Models;
using Shared.Storage;

var root = Path.GetFullPath(Path.Combine("artifacts", "backup-stop-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var signal = Path.Combine(root, "stop.signal");
var control = new BackupStopControl(signal);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS: " + name); }
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
Check(await control.RunAsync(_ => Task.CompletedTask, lifetime.Token) == "", "Ordinary work completes without a stop");
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var cancelled = false;
var job = control.RunAsync(async token =>
{
    started.SetResult();
    try { await Task.Delay(Timeout.Infinite, token); }
    catch (OperationCanceledException) { cancelled = true; throw; }
}, lifetime.Token);
await started.Task;
var stopId = Guid.NewGuid().ToString("D");
await File.WriteAllTextAsync(signal, stopId);
Check(await job == stopId && cancelled, "Stop signal cancels active asynchronous work and is acknowledged");
Check(!lifetime.IsCancellationRequested, "Stop does not cancel the agent lifetime");
var retried = false;
Check(await control.RunAsync(_ => { retried = true; return Task.CompletedTask; }, lifetime.Token) == stopId && !retried,
    "Pause prevents immediate queued retries");
Check(await new BackupStopControl(signal).RunAsync(_ => { retried = true; return Task.CompletedTask; }, lifetime.Token) == stopId && !retried,
    "Pause survives agent restart");
File.Delete(signal);
Check(await control.RunAsync(_ => { retried = true; return Task.CompletedTask; }, lifetime.Token) == "" && retried,
    "Explicit resume allows backup work again");
using (var shutdown = new CancellationTokenSource())
{
    shutdown.Cancel();
    var propagated = false;
    try { await control.RunAsync(token => Task.Delay(10, token), shutdown.Token); }
    catch (OperationCanceledException) { propagated = true; }
    Check(propagated, "Host shutdown is not swallowed as a user stop");
}
var backupId = Guid.NewGuid().ToString("D");
var successTime = DateTimeOffset.UtcNow.AddDays(-1);
var status = new StatusFileModel { AgentEnabled = true, LastSuccessfulBackup = successTime,
    LastOperationRequestId = "background", BackupProgressPercent = 70 };
BackupStopControl.MarkStopped(status, stopId, backupId);
Check(status.LastStopRequestId == stopId && status.LastBackupStatus == "Stopped", "Stop acknowledgement has its own ID and terminal status");
Check(BackupRequestProgress.IsComplete(status, backupId), "Pending manual request completes as stopped, not timed out");
Check(status.CompletedBackupRequestStatus == "Stopped" && status.BackupProgressPercent is null, "Stopped request clears progress without success");
Check(status.AgentEnabled && status.LastSuccessfulBackup == successTime, "Stop preserves agent enablement and last successful backup time");
var destination = Path.Combine(root, "existing.txt");
var staged = Path.Combine(root, "stage.tmp");
await File.WriteAllTextAsync(destination, "original backup");
using (var cancellation = new CancellationTokenSource())
using (var stream = new MemoryStream("replacement"u8.ToArray()))
{
    var stopped = false;
    try
    {
        await AtomicBackupFile.WriteAsync(stream, staged, destination, stream.Length, DateTime.UtcNow,
            () => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token);
    }
    catch (OperationCanceledException) { stopped = true; }
    Check(stopped && await File.ReadAllTextAsync(destination) == "original backup", "Cancellation before promotion preserves existing server copy");
    Check(!File.Exists(staged), "Cancelled staging file is cleaned up");
}
using (var cancellation = new CancellationTokenSource())
using (var stream = new CancelStream(cancellation))
{
    var stopped = false;
    try { await AtomicBackupFile.WriteAsync(stream, staged, destination, 8, DateTime.UtcNow, () => Task.CompletedTask, cancellation.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Check(stopped && await File.ReadAllTextAsync(destination) == "original backup" && !File.Exists(staged),
        "Cancellation during copy never exposes a partial replacement");
}
using (var stream = new MemoryStream("short"u8.ToArray()))
{
    var rejected = false;
    try { await AtomicBackupFile.WriteAsync(stream, staged, destination, 100, DateTime.UtcNow, () => Task.CompletedTask, default); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected && await File.ReadAllTextAsync(destination) == "original backup" && !File.Exists(staged), "Truncated entry cannot replace backup");
}
using (var stream = new MemoryStream("complete replacement"u8.ToArray()))
    await AtomicBackupFile.WriteAsync(stream, staged, destination, stream.Length, DateTime.UtcNow, () => Task.CompletedTask, default);
Check(await File.ReadAllTextAsync(destination) == "complete replacement" && !File.Exists(staged), "Completed entry is promoted normally");
Console.WriteLine($"All {checks} isolated stop checks passed. No real agent, server or backup files touched.");

sealed class CancelStream(CancellationTokenSource cancellation) : MemoryStream(new byte[8])
{
    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        await destination.WriteAsync(new byte[4], cancellationToken);
        cancellation.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
