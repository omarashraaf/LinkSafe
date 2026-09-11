# Missing server backup repair (1.0.4)

Previously, smart backup trusted its local confirmation history. Deleting server copies did not change the local file size/timestamp, so the agent incorrectly reported no changes.

Manual and logon full scans now POST bounded batches of relative paths and expected lengths to `/api/backup/check`. The server resolves the current configured upload destination, rejects disabled/unregistered clients and unsafe paths, and checks actual storage. Only matching server copies may suppress unchanged local files. Missing or truncated copies are repackaged and uploaded. Successfully uploaded files become confirmed for the remainder of that run, preventing repeated repair packages.

The check reports progress, reads metadata only, and does not enumerate the whole backup tree. It is a presence/length check, not a checksum-based corruption audit. Same-size content corruption and empty-directory-only deletion are not repaired by this feature. Files no longer present locally cannot be recreated from local backup; use another retained copy or version. Ordinary exclusions and size/access restrictions still apply.

Connection/server failures do not erase local confirmation history or report a verified no-changes result. Local changes can still be packaged for the offline retry queue; the agent remains running after a failed logon check. Older servers return an explicit update instruction. Existing API identity/authentication limitations are unchanged; this is not production security hardening.

## Update and acceptance

1. Install/update the server using setup 1.0.4. Existing configuration and backups are retained.
2. Download a newly generated client from that server. Exit the old tray app, open the new client, then Disable Agent and Enable Agent to replace the background binary.
3. Click Back up now. The client first shows the server-copy check and then packaging/upload progress. Missing eligible server copies should return under the same user/computer folders.
4. Run it again: intact unchanged copies should not upload again. Do not delete real data for testing; use disposable test files.

## Isolated verification

Build the server in Release, then run `dotnet run --project Tests/BackupRepairChecks/BackupRepairChecks.csproj -c Release`.

The harness injects isolated JSON stores, uses the real package builder, client HTTP check code, server presence service and upload/extraction service. It backs up ten fixture Documents files, deletes two server copies, verifies only those two are packaged, uploads them, checks their contents, deletes the full fixture Documents folder, and verifies all ten are recovered. It also covers intact-copy skipping, truncated copies, 500-file batching, unsafe paths, disabled clients, server failure, old-server detection and cancellation. It does not change live profiles, backups, services, scheduled tasks or configuration.
