# Server installer verification

## 1.0.10 startup readback correction

Fixed the 1.0.9 verification regression: Windows exports the trigger's user as an account name and drops the default RunLevel element. Verification now compares resolved SIDs and recognizes omitted LeastPrivilege, without accepting a different user or missing/global logon identity. Repair/removal ownership checks and the developer client script use the same SID comparison rule. The setup UI is version 1.0.10; the background agent and required compatibility version remain 1.0.9 to avoid needless replacement of a working agent.

Thirty startup checks passed, including read-only validation of the actual Windows-registered task, Windows-style exports, omitted defaults, same-name users in different domains, unresolved users and negative security/settings cases. No task, agent process or backup was modified. A new live task install and reboot were not performed.

Also passed: 24 client presentation checks, 36 installer/payload checks and developer-script syntax validation. The direct client EXE is 51,918,675 bytes, version 1.0.10.0, SHA-256 `6EF667769AFC788FD61496DBA309A72956403CCC33932A1F1FE6C48AE430CDF4`. The self-contained server installer is 202,261,104 bytes, SHA-256 `78C7E1830CFB2BD4271016C28F437CCDA6FDD36704C4F9489AA13C0BE4E30CC4`. It bundles client setup 1.0.10 and background agent 1.0.9. Packages remain unsigned; neither was deployed during verification.

## 1.0.9 verified login startup

Added explicit per-user task XML, battery-safe settings, restart-on-failure, unlimited runtime, checked scheduler exit codes/readback, and visible startup verification in the client. Worker failures return a nonzero exit code. Enable no longer marks a new installation enabled before task registration succeeds. No live task, process, backup or reboot was changed during verification. See `Tests/StartupChecks/README.md` for targeted tests and necessary live login acceptance checks.

Passed: 22 startup fixture checks, 27 backup-flow, 24 client presentation, 16 stop/resume, and 36 installer/package checks. Windows Task Scheduler also accepted the generated XML using validation-only mode (no task registration). The developer client install script passed PowerShell syntax validation. Real task registration under standard/elevated accounts and an actual reboot with the new client still require acceptance testing.

The 1.0.9 installer is 202,259,054 bytes; SHA-256 `6167D9763940212D3B335ED9C2EFED6E436E04F70BDB72C8D08981DB71783DFC`. Both bundled client executables report 1.0.9.0. The package remains unsigned and has not been deployed during these checks.

## 1.0.8 policy synchronization and client folder selection

Server policy is refreshed independently of backup uploads, and local folder overrides are stored separately from the cached server policy. The client can choose supported profile folders, custom local directories and drives, or revert to server control. Changed sources get a follow-up scan; existing stop/pause state is preserved. Server client details can reset a per-client policy to global defaults and show the last reported local override. Serialized registry mutations prevent heartbeat/registration from losing concurrent policy edits.

The 18 policy checks use fixture JSON, sources and in-memory HTTP, including an actual inventory/policy worker refreshing while a simulated upload remains blocked. Client presentation checks cover the new folder chooser. See `Tests/PolicySyncChecks/README.md` for scope and live acceptance steps. No live server, installed agent, scheduled task, user profile or backup has been modified by verification.

Final verification: 18 policy, 27 backup-flow, 24 client presentation, 23 backup repair, 30 restore, 16 stop/resume, 16 upgrade/migration and 36 installer checks passed (190 total). One restore symbolic-link fixture was skipped for unavailable OS privilege. The chooser was rendered and visually inspected. Server Razor compilation passed; the live server pages were not exercised. A build-cache file lock was cleared by shutting down only .NET build servers.

The final self-contained installer is 202,241,646 bytes, version 1.0.8.0, SHA-256 `0ADE1975B5F71A1BFA2BE0E18625C9952B9B7A6A8757E27787362F5AA344F423`. Both bundled Downloads templates report 1.0.8.0. The package is unsigned and has not been installed on the live server or a clean target machine during this verification.

## 1.0.7 managed updates and migration

The newer client checks the managed agent before use, downloads before replacement, pauses compatible running agents, replaces with rollback on activation failure, and removes only recognized older installed/rollback copies. Existing user pauses, settings and queue are preserved. A new Move data to this device action selects another backup device for the same claimed domain-qualified account and previews actual destination paths before copying missing files only. Existing local and server/source files are not deleted.

The 16 upgrade/migration fixture checks, 27 backup-flow checks and 20 client presentation checks passed. No real process, user profile, live configuration or backup was changed. The tests simulate executable activation, so real upgrade/recovery and affected-PC migration still need acceptance testing. See `Tests/ClientUpgradeChecks/README.md` for deployment steps, pre-1.0.6 upgrade limits, application-setting limits, and unchanged authentication restrictions.

Final 1.0.7 verification also passed 30 restore, 16 stop/resume, 23 deleted-backup repair and 36 installer/package checks: 168 passing checks total. One symbolic-link restore fixture was skipped for unavailable OS privilege. The self-contained installer bundles agent and client 1.0.7; no live upgrade or migration was performed.

## 1.0.6 stop backup

Added Stop backup with cooperative cancellation and persistent pause until Back up now. Background agent policy/heartbeat work remains enabled, queued packages are retained, and manual request completion reports Stopped without changing the last successful backup time. Server extraction now promotes only fully staged files, preserving existing copies on cancellation. Received packages cancelled during extraction are retained in quarantine and marked Stopped. Already committed files are not rolled back.

The 16 isolated stop checks, 27 backup-flow regressions, 23 deleted-backup repair checks, 18 client presentation checks and 30 restore checks passed. One restore symbolic-link fixture was skipped because the OS privilege was unavailable. Scans of unchanged files also observe cancellation. See `Tests/BackupStopChecks/README.md` for upgrade steps and limits. Both server and agent require 1.0.6 for this feature. No live backup/service/task was changed. Build-cache file locks were resolved by restarting only the .NET build servers, without changing security settings or running LinkSafe processes.

The final 1.0.6 payload passed all 36 installer/package checks, giving 150 passing checks in total. Live stop/resume on the affected PC still needs acceptance testing after upgrade.

## 1.0.5 queued-request presentation

Client UI 1.0.5 removes the false three-minute failure while a valid manual request is still queued behind a running backup. Waiting shows the background operation in a neutral label without a looping progress bar; acknowledgement switches to active progress and only matching completion ends the request. A disappeared signal has a separate grace period for the agent's acknowledgement write. A stopped agent still reports an error. Reopening/clicking with an existing queued GUID reuses that request rather than replacing it. Terminal upload-queued results are not shown as successful completion.

All 26 backup-flow checks and 15 client presentation checks passed. Tests include a 20-minute-old package-4 upload, pending/active/completed transitions, neutral queue styling, and existing backup regressions. This does not speed up uploads or prove a remote live upload completed. Agent 1.0.4 remains compatible and does not need restarting for this UI fix. No live backup/service/task was changed.

Final self-contained installer 1.0.5 passed all 36 package/setup checks (77 checks total for this update). A standalone client UI 1.0.5 is also available at `artifacts/ClientSetup/LinkSafeClientSetup.exe`; it reads the existing local configuration. Exit only the old tray UI and open the new client; do not disable the background agent while it is uploading. The combined installer includes this client for future downloads, but a server restart is not required to use the standalone UI fix with an existing 1.0.4 server/agent.

## 1.0.4 missing server backup repair

Added server-side presence/length checks before manual and logon smart scans so local history no longer hides deleted server copies. Both server and client must be updated. The isolated repair harness passed 23 checks, including real ZIP extraction, selective repair of two deleted copies, repair after all ten server copies were deleted, unchanged-copy skipping, batching and error handling. All 19 backup-flow regression checks also passed. See `Tests/BackupRepairChecks/README.md` for scope and acceptance steps. No live backup data or running services were changed.

Final package verification passed 36 installer checks, 30 restore checks and 11 client presentation checks, for 119 passing checks in total. One restore symbolic-link fixture was skipped for unavailable OS privilege. The first publish encountered a transient mapped build-cache file; an ordinary retry succeeded without modifying security settings or stopping live processes. Affected-PC acceptance remains required after updating server and client together.

## 1.0.3 missing-only restore update

Installer 1.0.3 bundles the updated server and both client apps. Restore compares the server manifest with local destinations before requesting file contents, downloads only missing files, and preserves existing files even when their contents or timestamps differ. Manual restore can use this PC's own backup; logon discovery continues to prefer another device. Failed or interrupted downloads do not create partial destination files and remain eligible for retry.

Verification: 30 isolated restore-flow checks, 19 backup-flow checks, 11 client presentation checks and 36 installer/package checks passed. One symbolic-link fixture was skipped because the OS privilege was unavailable; no privileges or policies were changed. Tests covered preserved local files, missing user-folder files, enabled additional sources, interrupted/truncated downloads, retry, unsafe paths and concurrent local file creation.

No live profiles, backup data, services or scheduled tasks were changed. Actual restore/logon acceptance on the affected PC remains required. Existing device-identity/authentication limitations are not fixed by this release. See `Tests/RestoreFlowChecks/README.md` for scope, limits and update steps.

## 1.0.2 user-folder and progress update

Fresh server/client binaries were bundled in installer 1.0.2. Verified file versions for both downloadable client executables are 1.0.2.0. All 19 isolated backup-flow checks, 11 client presentation checks and 36 installer/package checks passed. The package contains the 1.0.1 Windows PowerShell replacement fix as well.

The backup-flow fixture packages ten Documents files plus Desktop and additional-source data, checks remapped and legacy confirmation history, new-file detection, and request-specific completion. The client presentation checks show/hide the busy indicator correctly. No live client queue, user profiles, remote server data, services or tasks were altered by these checks. Acceptance on the affected PC is still needed; see `Tests/BackupFlowChecks/README.md` for the update steps and scope.

## 1.0.1 replacement regression

Reproduced the user's exact `The path is not of a legal form` error on Windows PowerShell 5.1.26100.8875 using fixture files only. A plain `$null` is converted to an empty .NET string; `[System.Management.Automation.Language.NullString]::Value` correctly supplies the absent backup filename. Both the JSON and client-template replacement calls now use it. LAN rollback also attempts service restart even when its configuration write fails.

Added Windows PowerShell regression coverage for the original failure, preservation of the original file, the corrected replacement, JSON configuration update and rollback, binary replacement, and a locked destination. The test invokes only isolated file operations; no installer/service/firewall operations or execution-policy changes are performed.

Result: all 36 checks passed (29 package/UI checks plus 7 Windows PowerShell replacement checks). Installer 1.0.1 published successfully. Full installation/recovery on the user's destination server remains to be verified there.

References: [PowerShell NullString](https://learn.microsoft.com/en-us/dotnet/api/system.management.automation.language.nullstring) and [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace).

Verified in the development workspace without installing the server or changing the live server, firewall, agent task, configuration or backups.

- Server build with Windows-service lifetime: passed, zero warnings/errors.
- Installer WinForms build: passed, zero warnings/errors.
- 29 C# checks passed: ordinary/unsafe Windows paths, traversal, reserved names, manifest hash failures, duplicate and unlisted ZIP entries, actual bundled payload extraction and hashes, required server runtime and service integration DLL, CSS, both client executable templates, absence of server-config.json, primary UI controls, and layout at two window sizes.
- Visually inspected the rendered setup window. The initial folder button overflow was fixed before the final publish.
- PowerShell helper AST syntax validation passed in the available PowerShell host. Windows PowerShell runtime validation was blocked by the host execution policy. No policy was changed or bypassed.
- No setup Install, Enable LAN, or Uninstall operation was executed on the development machine.

## Clean-machine acceptance checklist

On a disposable Windows x64 server with an organization-approved script execution policy, verify:

1. Install creates an auto-start LocalSystem service and admin-only NTFS backup/config directories.
2. Before creating the admin account, a second device cannot access the loopback-bound server.
3. After admin creation and Enable LAN, another device can access the correct IP/port on the trusted subnet; public-profile access remains closed.
4. Web UI CSS loads, the client setup download runs, and a small backup/restore completes.
5. Reboot starts the service without an interactive login.
6. Update retains admin credentials, configuration and backups. An intentionally failing server startup rolls back binaries in a disposable fixture.
7. Uninstall removes only the server service/binaries and installer firewall rule, while config/logs/backups remain; reinstall uses the retained configuration.

This package contains the current MVP, including the already documented backend/authentication limitations. Packaging verification is not a production security assessment.
