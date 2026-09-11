namespace Shared.Models;

public static class BackupRequestProgress
{
    public static bool IsWaitingForAgent(StatusFileModel status, string requestId, string queuedRequestId) =>
        !string.IsNullOrWhiteSpace(requestId) &&
        !IsComplete(status, requestId) &&
        status.LastOperationRequestId != requestId &&
        string.Equals(queuedRequestId.Trim(), requestId, StringComparison.Ordinal);

    public static string WaitingMessage(StatusFileModel status) =>
        status.LastOperationFinishedAt is null && !string.IsNullOrWhiteSpace(status.LastOperationMessage)
            ? "Request queued. The agent is finishing its current work: " + status.LastOperationMessage
            : "Request queued. Waiting for the agent to start this scan. This is not a backup failure.";

    public static bool IsComplete(StatusFileModel status, string requestId) =>
        !string.IsNullOrWhiteSpace(requestId) &&
        string.Equals(status.CompletedBackupRequestId, requestId, StringComparison.Ordinal) &&
        status.CompletedBackupRequestStatus is "Success" or "NoChanges" or "Queued" or "Failed" or "Stopped";
}
