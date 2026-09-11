# Backup flow fix (1.0.2)

Run `dotnet run --project Tests/BackupFlowChecks/BackupFlowChecks.csproj -c Release` from the repository root.

The test uses synthetic Documents/Desktop and an additional local source under a unique `artifacts/backup-flow-tests` directory. ZIPs are written only to that fixture queue, never the real agent queue. No HTTP requests, real user-folder scans, scheduled task changes or restores occur.

Checks cover all ten Documents files alongside Desktop/additional-source files, progress callbacks, unchanged-file skipping, new-file detection, changes to the archive destination, revalidation of legacy state, and request-specific completion (old successes do not complete a new request).

Identified causes corrected:

- The client previously accepted any old terminal operation status immediately after signalling a new backup. Requests now use unique IDs; only the matching completed request ends the progress display.
- Refreshing the client dashboard previously wrote an old status snapshot back to disk, overwriting newer worker progress. Refresh is now read-only.
- Uploading a queued package previously returned early instead of scanning all selected sources. Manual backup now continues scanning.
- Confirmed file history tracked local paths only, not destination ZIP paths. Files previously uploaded under a drive path are now resent when they should appear under a user-folder path. Old history without an archive path is revalidated once.
- The new client checks the installed agent version. Disable/Enable updates an old agent using a temporary download and atomic replacement, retaining config and queue. No client processes are stopped by the development tests; stopping the installed agent occurs only when the user chooses Disable Agent.

## Deploy

On the destination server, run the new 1.0.2 server setup and choose Install / update. This refreshes both client download templates. Generate a new client setup from Downloads and open it on the affected user's PC. Close the old tray UI first, then use Disable Agent and Enable Agent in the new UI to replace the background executable. Finally click Back up now.

The first scan may upload unchanged files once because legacy state did not record backup destination paths. This does not delete old backups. Subsequent scans return to changed-files-only behavior. Scan/upload progress is indeterminate where the total is unknown; packaging shows per-package candidate progress, not a fabricated whole-backup percentage.

Acceptance on the affected PC is still required: verify its resolved source paths in the agent log (`Backup source ...`), compare all selected user folders at the server destination, and check that the final completion corresponds to the new request. Unavailable, excluded, oversized and unreadable sources/files remain subject to policy and are not made readable by this update. Known unrelated backend/security findings remain documented in `Tests/ReviewChecks/REVIEW.md`.
