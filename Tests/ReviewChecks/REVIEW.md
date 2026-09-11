# LinkSafe review, 2026-09-09

No production code was changed during this review. The test runner uses compiled Debug assemblies and redirects server service stores into its own fixture directory before registering or uploading data. Existing backups, client registry, and queue were not modified.

## Runtime observations

- Neither the agent nor a listener on port 5055 was running initially.
- The most recent agent log records a disk-full IOException during ZIP finalization at 2026-09-09 12:02:53 local time, followed by StopHost and shutdown.
- C: had approximately 1.4 GiB free at inspection. Queue contained 694 files totaling 13,449,642,513 bytes (about 12.5 GiB). Files were retained.
- The installed task is enabled for logon but does not start on batteries, stops on batteries, and has a 72-hour execution limit.
- Started the published server temporarily: health HTTP 200 (392 ms), login page accessible, stylesheet HTTP 200 (78 ms). Anonymous dashboard request redirects to login (302).
- Anonymous restore-options request with an invented account returned HTTP 200 (57 ms). No real user's restore package was requested.
- Stopped the temporary server after the checks, restoring its initial stopped state.

## Findings

1. **P1: API identity is unverified.** `LinkSafe/LinkSafeServer/Controllers/RestoreController.cs:13` and `:28` accept user/computer names without authentication or ownership validation. Razor Page authorization in `Program.cs:45` does not protect controllers. Anonymous discovery was verified over HTTP; download authorization failure is established by code inspection. Registration and uploads also lack authenticated device identity. A network client can impersonate another account.

2. **P1: ZIP containment allows sibling-prefix escape.** `LinkSafe/LinkSafeServer/Services/BackupStorageService.cs:262` checks a path prefix without a directory separator boundary. For root `.../PC`, `../PC-other/escape.txt` is accepted. This was reproduced against the real validation method without extracting the payload. Browse/download containment checks have the same pattern.

3. **P1: Agent exits on disk exhaustion.** `LinkSafe/LinkSafeClient/LinkSafeClientAgent/Worker.cs:10` allows exceptions from the startup scan to escape and stop the host. The real log confirms this. There is no queue disk reserve/backpressure mechanism before creating new archives. After termination, file watching and retries cease. A logon task being enabled does not mean the worker is healthy.

4. **P1: Modified file can be acknowledged without being saved.** `LinkSafe/LinkSafeServer/Services/BackupStorageService.cs:298` treats equal length and timestamps within two seconds as identical. A fixture containing OLD locally and NEW in the upload was incorrectly treated as current. The upload loop then counts the skipped entry as processed; the agent can confirm it and stop trying to send the missing content.

5. **P2: UI can overwrite newer worker state.** `LinkSafe/LinkSafeClient/LinkSafeClientSetup/MainForm.cs:361` reads status before an HTTP request and writes the entire stale snapshot at `:385`. The test reproduced Success reverting to Packaging. Per-instance semaphores in JsonFileStore do not make a read/modify/write sequence atomic, especially across the setup and agent processes. This explains stale dates and misleading progress.

6. **P2: Run Backup Now can finish from a previous job's status.** `LinkSafe/LinkSafeClient/LinkSafeClientSetup/MainForm.cs:300` accepts any final status immediately after signalling a backup, without checking a new operation ID or start timestamp. An old Success/NoChanges can end the UI wait before the requested scan starts. Each chunk also publishes Success before the entire scan ends. Code-inspection finding.

7. **P1: Restore overwrites are not atomic.** `LinkSafe/LinkSafeClient/LinkSafeClientAgent/Services/RoamingRestorePromptService.cs:217` and `LinkSafe/LinkSafeClient/LinkSafeClientSetup/MainForm.cs:768` truncate the destination before copying. A copy failure can leave a damaged local file. There is no staging/atomic replacement or rollback, and one locked file aborts the remaining restore. Code-inspection finding; no user restore was executed.

## Verification

Run from repository root:

```powershell
dotnet build LinkSafe.slnx --no-restore
dotnet run --project Tests/ReviewChecks/ReviewChecks.csproj
```

Solution build: zero warnings/errors. Regression runner: **12 passed, 3 failed**; exit code 1 intentionally reports the reproduced bugs.

Passed: normal ZIP path; ordinary parent traversal rejection; absolute path rejection; new-file detection; unchanged-file skipping; modified-file detection; low/high timestamp clamping; isolated registration; exact-byte upload extraction; overwrite with versioning; quarantine of an unsafe package.

Failed: sibling-prefix containment; same-size recent modification accepted as current; stale UI snapshot preserves newer agent status.

The upload tests exercise real server services with isolated JSON stores and small in-memory multipart file objects, not HTTP uploads. Live HTTP checks were read-only. A real logoff/logon cycle, full profile backup, full restore, WinForms visual interaction, and NTFS access as a separate normal user were not tested. The timestamp fixture normalizes the saved file time to the decoded ZIP timestamp to avoid timezone-dependent false results.
