# Policy synchronization checks

Build the server first, then run:

```powershell
dotnet build LinkSafe/LinkSafeServer/LinkSafeServer.csproj -c Release
dotnet run --project Tests/PolicySyncChecks/PolicySyncChecks.csproj -c Release
```

The 18 fixture checks cover stale base configuration, server policy/timing updates, local replacement rather than additive selection, protection retention, reset-to-server, explicit empty overrides, unsafe custom paths, server-scoped cache, scan-key changes, and concurrent registry heartbeat/policy saves. A real DriveInventoryWorker uses an in-memory HTTP handler while a simulated upload remains blocked, proving policy refresh is independent of uploads. The real package builder captures an already-existing file from a newly selected Desktop fixture alongside previous Documents history.

All JSON, queue and source files are under a unique `artifacts/policy-sync-tests` directory. No real server requests, tasks, backups or installed agent updates are performed. Windows identity and ready-drive discovery are read-only. ClientUiChecks separately renders the chooser without integration and checks default/local mode controls.

Live acceptance after upgrading both server and agent to 1.0.8:

1. Start an enabled client in Use server settings mode. Save different folders in its server client details; confirm the displayed policy timestamp and folder list update, then verify an existing test file is backed up.
2. Select Use server defaults on the server and change global Settings; confirm this client follows the global folders.
3. Choose folders locally with Use server settings unchecked. Change server folders again: local folders should remain, while server limits/exclusions still apply. The server should show the reported local override within roughly two minutes.
4. Switch the client back to Use server settings. Confirm the latest server folder selection returns and is scanned, without a restart.
5. During a large upload, repeat a server edit. The list should update independently; current work may finish before the follow-up scan. Verify a removed source does not cause backup deletion.
6. Stop backup, change selection, and confirm it stays paused. Back up now resumes with the new selection. Reconnect a selected drive and confirm it is watched/scanned again.

No new client policy-write endpoint was added. Existing MVP identity/authentication and machine-wide client configuration limitations are unchanged; this is not a security hardening release. Use only on a controlled trusted LAN pending authentication hardening. Clean-device installation and actual affected-PC behavior require live acceptance testing.
