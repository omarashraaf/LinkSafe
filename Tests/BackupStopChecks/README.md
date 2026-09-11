# Stop backup (1.0.6)

The client provides Stop backup next to Back up now. It writes an atomic stop request; the agent monitors it while scanning, packaging, uploading and retrying. Cancellation is cooperative: the UI shows a request until the agent acknowledges it, rather than claiming an immediate successful stop. The agent remains enabled and continues policy/heartbeat work.

The stop marker persists across restarts. Automatic backups and queue retries stay paused until Back up now (or the updated agent's `--backup-now` command) clears the marker. Pending manual requests finish with Stopped status; completed queue packages are retained. Stop does not delete original files, existing backups, version history or pending ZIP packages. Partial local ZIP creation is cleaned up by the package builder. A new full scan on resume catches changes that occurred while paused.

The server stages each file before promotion, so cancellation does not replace an existing backup with a partial file. A cancelled extraction records Stopped and retains the received package in quarantine. Files committed before cancellation stay committed; a request fully accepted by the server may finish before client cancellation reaches it. This is not rollback or an atomic transaction across a whole package.

Update both server and agent to 1.0.6. Stop is not supported by older agent binaries and the UI shows an upgrade instruction. After updating the server, download the new client, exit the old tray UI, then Disable agent and Enable agent once to install the new background binary. Schedule the initial upgrade outside active uploads.

Run `dotnet run --project Tests/BackupStopChecks/BackupStopChecks.csproj -c Release`. Tests use isolated fixture paths and verify cancellation propagation, pause/restart/resume, request acknowledgement, preserved success timestamps, and atomic file replacement with cancelled/truncated streams. ClientUiChecks additionally verifies that the Stop action is allowed while a manual backup is active. No live agent tasks, network transfers, user profiles or production backups are altered. Real affected-PC acceptance remains necessary.
