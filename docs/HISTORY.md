# Historical LinkSafe Notes

These are retained development/release notes, not current installation instructions.
Versions, paths, UI controls, security claims and test counts below may have been superseded.
Read the root README and current code first. Clients now use per-user LocalAppData,
operational pages update automatically, and the API does not enforce the original
registration-token security design. Do not deploy from an old command here.

## Automatic live status (server 1.0.20)

Clients, Dashboard and Logs check for new server data every five seconds while the tab is visible. The page is not reloaded. Request states (Queued, Blocked, Running, Success, etc.), client status, newly registered/removed clients, dashboard summaries and log lines update automatically. These are the latest **reported** values: an agent's own polling/report interval still applies. This does not force offline/paused agents to back up or turn queued requests into successes.

Search filters, bulk checkboxes, action choices, keyboard focus on existing client controls, and unsaved bulk settings remain intact. Live updates do not replace editable settings forms. Hidden tabs pause polling; returning resumes it. Requests do not overlap, time out after ten seconds, and back off to at most thirty seconds between failed attempts. The status indicator distinguishes Live, connection loss (last received data), and an expired session requiring sign-in. Polls require the normal admin cookie, do not follow login redirects, use no-store responses and never submit mutations. A poll does not clear action notices.

Storage usage is approximate and measured asynchronously about once a minute, with one scan at a time. Calculating/Unavailable is shown instead of blocking dashboard refresh or reporting a permission failure as zero bytes. Reparse-point entries are skipped. Log previews read only the last 256 KiB and display at most 200 lines, preserving the reader's scroll position. Download the log for older content.

Deploy with Server Setup **1.0.20**. No new client agent is required. These changes are only in the built artifacts until the server is updated. Regression checks in `Tests/RemoteBackupChecks/LiveChecks.cs` cover authentication, caching, current rendered status, bounded/encoded logs and cached storage measurement; isolated browser checks cover actual automatic status transitions, preserving edits, retry/reconnection, expired login and desktop/mobile rendering.

## Bulk client actions (server 1.0.19)

On **Clients**, select up to 200 computers, choose an action and click **Review selection**. Confirm the displayed computers before applying. The header checkbox selects visible search results only; previously selected hidden results remain selected with a visible count warning. **Clear selection** clears both visible and hidden choices.

Actions: request backups, replace server-selected folders/additional local paths, restore server defaults, enable/disable server access, or remove registry entries. Settings use one locked registry update, validate every selected target and all paths before saving, and leave unselected clients unchanged. Changing folders replaces both folder/path lists for the selected clients; timing and exclusions remain global and unchanged. PCs with local overrides keep their own choices until **Use server settings** is selected in their agent. Unavailable drives on a PC cannot be backed up.

Backup requests use the existing agent 1.0.16+ polling flow, reuse pending requests and report skipped disabled/older clients separately. Queued is not completed. Removal never deletes backups or uninstalls agents; active agents may register again. Disable changes server access, not Windows startup or a running local operation. Admin login, POST antiforgery checks and an explicit confirmation are required for bulk changes. Stale/invalid selections are rejected without registry changes. Backup dispatch is per-client, so already queued requests survive a later interruption.

Install/update using **Server Setup 1.0.19** on the server to deploy the Web UI and handler changes. Client 1.0.16 is still bundled; a client upgrade is not required for bulk folder/registry operations, and 1.0.16+ is required for server-requested backups. Building locally does not update a running remote server.

`Tests/RemoteBackupChecks` includes bulk HTTP authorization, antiforgery, confirmation, selection/path validation, settings/defaults, duplicate backup dispatch and registry-only removal tests. Browser QA uses isolated fixture records, not live computers or backup jobs.

## Server Setup 1.0.18: Windows PowerShell client-list fix

Setup 1.0.17 resolved a same-server backup share but could fail when remapping multiple registered clients on Windows PowerShell 5.1. Its JSON array was treated as one record, combining different destination paths. Setup 1.0.18 enumerates records correctly on both PowerShell 5.1 and 7, preserves empty/single/multiple-client arrays, and checks migration destinations before extracting the payload or stopping the service. Unsafe paths remain rejected. An invalid record now identifies its destination in the error message.

Run the rebuilt `artifacts/ServerSetup/LinkSafeServerSetup.exe` on the server as administrator and choose **Install / update**. There is no need to uninstall, delete client records, change the backup location or remove backups. The installer remains self-contained and bundles server/client 1.0.16. After successful installation, the **LinkSafe Server** Windows service runs in the background with Automatic startup; Setup and the browser can be closed.

Tests include the original array-wrapping failure and corrected expression on Windows PowerShell 5.1, plus persisted registry migration/rollback with zero, one, two and ten clients on PowerShell 7. These isolated checks do not install a service or modify live backups.

## Request backups from the server (1.0.16)

On **Clients**, admins can click **Back up now** for a registered, enabled computer running agent 1.0.16 or later. The CSRF-protected admin form creates a persistent request in `C:\ProgramData\LinkSafeServer\remote-backup-requests.json`. Repeated clicks reuse the same active request. Older agents display an update requirement rather than accepting a request they cannot execute.

The agent polls independently about every 15 seconds over the API; no SMB writes, inbound client port or remote shell is used. It saves receipt/result in `%LOCALAPPDATA%\LinkSafeAgent\remote-backup-request.json`, waits for current work to finish, then invokes the existing manual scan/upload flow using its effective folder policy (including local overrides). Offline requests remain queued. Locally paused or disabled agents are not forcibly enabled/resumed; the request remains waiting/Blocked until the user resumes. If the agent is not running at all, it cannot report Blocked and remains Queued until it starts. Interrupted work reports Stopped; packages queued after an upload failure stay available for the normal retry flow, but the remote attempt is not marked successful. Process crashes may repeat an unconfirmed scan after restart; completed request IDs are retained to avoid repeating acknowledged work.

The **Requested backup** column separates Queued, Running, Blocked and terminal results from the client's general last-backup status. Use **Refresh status** to see the latest message and report timestamp. A stale Running report is not proof that a disconnected agent is still working. Latest request per user/computer is retained, not a full command history. A request never changes selected folders or deletes backup data.

Deploy the rebuilt **server installer 1.0.16** on the server, then distribute the newly generated **client setup 1.0.16** and enable/update each worker to **1.0.16**. Replacing only the old client template cannot add the new server endpoint or button. Building locally does not update a remote server. Existing older servers remain compatible with local backups; agent polling treats their missing dispatch endpoint as unsupported.

Security boundary: request creation requires the admin cookie login and antiforgery protection. Agent polling/status reports follow the existing trusted-LAN registration identity model, which does not cryptographically authenticate individual devices. Keep the service on a trusted restricted network; this feature is not a replacement for authenticated device enrollment, HTTPS, or production-grade endpoint identity. Never expose the MVP directly to the Internet.

Verification: `Tests/RemoteBackupChecks` exercises persistence, duplicate delivery, matching completion, failure/interruption, version/disabled guards, the real Razor form and API, admin authentication and CSRF using isolated files and a loopback test host. No live backup is dispatched by these tests.

## Repair a protected legacy startup task

If Enable reports that Windows blocked removal of the old `LinkSafe Client Agent` task, a standard-user client cannot remove its administrator-protected task. An administrator can run `Scripts/Repair-LinkSafeLegacyTask.ps1 -UserName 'DOMAIN\user'` on the affected PC. Supply the actual backup user's account, not the administrator performing the repair. `-WhatIf` performs read-only ownership/executable validation without elevation. The script checks the exact root-level legacy task, its user SID and its single agent executable action, then stops/removes that task only and verifies removal. It does not change ACLs or delete client state, queued data or server backups. After repair, close the elevated window and run the updated client under the normal backup user, then click Enable agent. Do not run the backup client under a different administrator account.

## Manual backup startup-task correction (client 1.0.15)

Client 1.0.14 could fail before queuing a manual backup with `The system cannot find the file specified. (0x80070002)` after the legacy startup task had already been removed. Adding a drive was not the cause: every manual backup passed through the old-task cleanup. Windows COM translates a missing task into `FileNotFoundException`, which bypassed the previous `COMException` handler. Client 1.0.15 treats only missing-task/path results from that optional lookup as already cleaned up. Permission errors and unexpected scheduler failures still stop cleanup; other users' tasks are never removed.

Use client setup 1.0.15 with worker 1.0.14. The rebuilt server installer 1.0.15 bundles this pair. Updating local build artifacts does not update the remote Downloads page. Replace its setup template with the new build, generate a fresh download, exit the old tray window and open the new client as the normal user. Existing per-user selections, configuration, queue and startup tasks are retained. No drive formatting, folder deletion or permission changes are needed. Regression checks include the actual read-only Windows missing-task call and verify that permission/unexpected errors still propagate.

## Standard-user installation fix (1.0.14)

Client setup and worker 1.0.14 store all writable client data in `%LOCALAPPDATA%\LinkSafeAgent` for the Windows account running the app. This includes config, status, policies, queue, logs, signals, restore staging and `bin\LinkSafeClientAgent.exe`. The client creates a private ACL granting access only to that user, SYSTEM and Administrators. No Everyone/Domain Users grant or writable machine-wide directory is required. The UI remains in `%LOCALAPPDATA%\Programs\LinkSafeClient`. Login tasks now include the user's SID in their name, so users on the same PC no longer share a task or queue. Server storage and its ACLs are unchanged.

**Upgrade:** exit the old tray window, download the new setup and open it as the actual backup user, without Run as administrator. Click Enable agent once to initialize this user's new worker and login task. The server must provide both client setup and agent 1.0.14. Normal Windows task registration is supported; organizational restrictions can still require IT assistance. An old protected/elevated startup task may require one-time administrator removal for its owning user. The new client retires only a recognized legacy task owned by the same SID and only stops the matching old agent in that user's session, after the replacement download has been verified. It does not delete other users' tasks or the old shared directory.

**Legacy data:** `C:\ProgramData\LinkSafeAgent` is retained unchanged, including old queued packages and logs. Only its server address is reused when no new per-user config exists. Shared history, identity, restore consent, local overrides and queues are not automatically trusted or imported into a different user's workspace. A fresh scan backs up current files under the same server user/computer identity; existing server backups remain available. Re-select any local folder overrides. If old uploads were pending and their original files no longer exist, IT must review/recover those retained ZIPs; the new worker does not automatically retry the old queue. New stop signals, status and logs are in the per-user path above.

Tests: `Tests/ClientWorkspaceChecks` exercises private ACLs and configuration/queue/log writes with a non-elevated token, SID separation and conservative legacy handling. It uses an isolated profile fixture and never grants broad permissions. `Tests/StartupChecks` validates SID-named startup operations. Both developer scripts and packaged UI use the new paths.

## Simpler client dashboard (client 1.0.13)

The main screen now contains backup status, one **Back up now / Stop backup** action, one **Enable agent / Disable agent** action, folder selection and a **Restore data** menu. Restore missing files and move data from another device remain distinct choices in that menu. Buttons match in size; the server address is vertically centered inside a matching input frame. Routine registration, startup, restore preferences and logs are under **Connection & details**. Errors and startup warnings remain visible; startup repair is available in details even when the agent is already enabled.

Buttons reflect reported background work as well as manual requests, lock during transitions, and block conflicting backup/restore/agent actions. The underlying backup, restore and startup worker is unchanged (agent 1.0.11). Exit the previous client using its tray menu, then run `artifacts/ClientSetup/LinkSafeClientSetup.exe`. Opening it updates the installed UI and shortcuts for an existing installation without toggling the working agent; first-time users still click Enable agent. Update the server installer (1.0.13) to distribute the new UI through its Downloads page. Screenshots and presentation checks are under `artifacts/ClientDesign` and `Tests/ClientUiChecks`; no live install is changed by building.

## Client tray and Windows app registration (client 1.0.12)

Run the updated client and click **Enable agent** once. It installs the client window in `%LOCALAPPDATA%\Programs\LinkSafeClient`, adds **LinkSafe Client** and **Uninstall LinkSafe Client** to this user's Start menu, and registers **LinkSafe Client** under Windows Settings > Apps > Installed apps. The executable is independent of the original download. The client keeps its saved server address; the background worker remains version 1.0.11.

The existing scheduled task still handles login backup. A separate current-user Run entry starts the client with `--tray` at login without opening its dashboard, starting another worker, running upgrade downloads, or duplicating the worker's restore prompt. Windows decides whether the icon is in the taskbar or its hidden-icons overflow. Double-click it or choose Open to display the dashboard. Opening another copy reopens the existing client in that Windows session. Closing the dashboard hides it; Exit closes only the tray window. Sign-out/shutdown is no longer treated as a request to hide the window. Disabling the agent also removes tray auto-start; its Start menu shortcut remains available.

Uninstall from Installed apps, the Start menu shortcut, or the tray menu requires confirmation. It removes this user's startup task, stops the managed agent, removes installed executables and app entries, and retains configuration, logs, queue and server backups. Ownership/permission failures are shown instead of deleting another user's installation. Shared machine-wide agent data still has the MVP's single-user-installation limitations. Exit older pre-1.0.12 client windows once when upgrading; those older windows did not support single-instance activation. If Windows Startup apps has explicitly disabled the tray entry, re-enable LinkSafe Client there.

The direct client build is `artifacts/ClientSetup/LinkSafeClientSetup.exe`. For future downloads from the server, update it using `artifacts/ServerSetup/LinkSafeServerSetup.exe` (installer 1.0.12 bundles the new client). No running installation is changed merely by rebuilding. Installation checks use fixture directories and a temporary registry subtree, not live tasks, configuration or backups.

## Empty nested folder backup (1.0.11)

Back up now and login scans now include newly created subfolders even when they contain no files. Folder entries are confirmed only after server upload succeeds, and subsequent scans verify that they still exist on the server. Missing server folders are recreated without deleting local data or repeatedly uploading unchanged folders. Excluded folders and reparse points remain excluded. Folder-only ZIP packages correctly report zero files; their folder entries are tracked separately by type in the existing state records.

Update the server first using `artifacts/ServerSetup/LinkSafeServerSetup.exe`, then generate/download the new client from its Downloads page and run it on the client PC. Both server and agent need 1.0.11 for directory presence verification. Existing settings, logs and backups are retained by the installer. Building these artifacts does not update a running or remote installation automatically.

Scope: nested folders within selected sources during manual/login scans. The standalone file-watcher path and missing-file restore still operate on files; restoring empty directories is not included in this correction. Run `Tests/BackupRepairChecks` after a Release server build to exercise isolated real ZIP upload/extraction, folder verification, missing-folder repair and legacy-server handling.

## Startup verification correction (client 1.0.10)

The 1.0.9 client could show "Startup is not bound to this Windows user's login" after a successful task installation. Windows exports the login identity as an account name and omits default XML values; the corrected client compares resolved SIDs and recognizes those defaults. This is a client-setup correction, not a backup-data failure. Client setup 1.0.10 works with the existing background agent 1.0.9. The direct client EXE is `artifacts/ClientSetup/LinkSafeClientSetup.exe`; existing installations retain their saved server address. Update the server installer if you want its Downloads page to generate the corrected client setup.

## Verified login startup (1.0.9)

Click **Enable agent** in the updated client to install/repair the startup task. The client verifies the task for the current Windows user before reporting success, and displays a startup warning if its configuration is missing or incorrect. The agent runs silently at login, including on battery, with a short delay, restart-on-failure and no runtime cutoff. Opening the client window/tray automatically is not part of this task. Stop backup pauses still persist until Back up now. Old elevated tasks may require one-time administrator permission under the same Windows account to repair; failures are now shown rather than ignored. See [startup checks and deployment steps](../Tests/StartupChecks/README.md).

## Folder policy synchronization (1.0.8)

Update the server with the rebuilt installer, then generate/download a new client setup from Downloads and run it on each PC. The new client requires agent 1.0.8; changing source code alone does not update installed agents or a remote server's Downloads templates.

- The agent checks server policy independently of uploads, on a 15-second loop plus request time. Offline failures keep the last successful policy and retry. The client displays the last successful policy synchronization time.
- In the client, choose **Choose backup folders**. Leave **Use server settings** checked to follow the server, or uncheck it to override the user folders, browser/AppData profiles, additional folders and drives for this PC. The picker accepts full local paths, not SMB sources. An empty override means no newly scanned sources.
- Local overrides replace server folder choices, but server exclusions, protected source paths, size limits and timing still apply. Enable **Use server settings** again to apply the latest cached server selection. Local preferences are stored separately in `C:\ProgramData\LinkSafeAgent\local-backup-selection.json`; the agent writes `server-policy.json` independently so refreshing policy cannot erase local choices.
- On the server's client details page, **Save backup selection** creates a per-client server override. **Use server defaults** clears that override so future global Settings changes apply. A local agent override takes precedence over both. The server shows the last reported local override, refreshed approximately every two minutes while connected.
- Changed sources trigger a scan of existing files without waiting for new watcher events or the next logon. Long runs refresh policy between packages; the follow-up scan waits for current work. Reconnected selected drives rebuild watchers and trigger a scan. Stop backup remains paused until Back up now, even if the policy changes.
- Removing a source does not delete existing server backups or queued packages. An already packaged/uploading file can still finish, and retained queued packages can retry. This is not delete synchronization. Login scanning remains enabled; this change does not reintroduce periodic full scans.

Checks and deployment acceptance steps: [Policy synchronization checks](../Tests/PolicySyncChecks/README.md).

## Install on another server with one EXE

The portable installer output is `artifacts/ServerSetup/LinkSafeServerSetup.exe`.
It contains the Web UI/API, the .NET runtime, and the latest client setup/agent templates.
Only this EXE needs to be transferred; do not transfer your development folder or live ProgramData.
See [Server Setup Guide](../Installer/README.md) for installation, LAN access, updates and uninstall.

Build it with `pwsh -NoProfile -File Scripts/Build-LinkSafeServerSetup.ps1`.
This is the recommended installation path; the older developer scripts are not the packaged installer.

LinkSafe is a secure Windows backup system with two parts:

1. `LinkSafeServer`: ASP.NET Core Web UI + API for admin operations and client uploads
2. `LinkSafeClientAgent`: silent Windows background agent plus a small WinForms setup app

The core security model is server-mediated backup. Clients do not write to SMB shares and do not receive NTFS access to backup storage. They upload ZIP packages over HTTP/HTTPS to the LinkSafe Server API, and only the server writes into the protected backup root.

## Why the design avoids direct SMB backup

Direct client writes to a shared backup folder create the exact problems LinkSafe is meant to avoid:

- normal users can gain direct write/delete access to backups
- clients can browse backup content they should not see
- accidental or malicious deletes become much easier
- `/MIR`-style sync behavior can erase retained backup data

LinkSafe instead uses:

- server-side client registration and policy validation
- server-side username/computer sanitization
- safe ZIP extraction with zip-slip protection
- optional NTFS lockdown on `D:\LinkSafeBackups`
- server-side versioning before overwrites

## Solution layout

```text
/LinkSafe
  /LinkSafeServer
  /LinkSafeClient
     /LinkSafeClientAgent
     /LinkSafeClientSetup
  /Shared
/Scripts
README.md
```

## Server overview

`LinkSafeServer` runs on Windows Server and serves:

- admin web UI at `http://BACKUP-SERVER:5055`
- client API endpoints under `/api/...`

The web UI includes:

- Dashboard
- Settings
- Clients
- Backup Jobs
- Logs
- Restore / Browse Backups

The first run flow is:

1. browse to the server URL
2. create the first admin username/password
3. sign in using cookie authentication
4. configure backup root and backup policy
5. apply secure storage permissions

Server configuration is stored at `C:\ProgramData\LinkSafeServer\server-config.json`.

Server runtime data is stored at:

- `C:\ProgramData\LinkSafeServer\registered-clients.json`
- `C:\ProgramData\LinkSafeServer\backup-jobs.json`
- `C:\ProgramData\LinkSafeServer\user-folder-map.json`
- `C:\ProgramData\LinkSafeServer\Logs\backup-server.log`

## Backup storage structure

Default backup root:

```text
D:\LinkSafeBackups
  _Incoming
  _Quarantine
  _Versions
  _Logs
  mhesham
    PC-001
      Documents
      Desktop
      Downloads
      AppDataRoaming
```

Clients upload packages. The server stores them in:

`D:\LinkSafeBackups\{username}\{computerName}\`

The server never mirrors client-side deletes into the backup store.

## Secure storage permissions

The `Apply Secure Storage Permissions` action is intended to lock down the backup root so that normal users do not have direct read/write/delete access. The current MVP applies Windows ACLs for:

- `Administrators`
- `SYSTEM`
- the configured LinkSafe service account
- the optional backup operators group

Normal users should not be granted NTFS access to the backup root. Admin-only sharing such as `\\BACKUP-SERVER\LinkSafeBackups$` should remain optional and disabled by default.

## Client overview

`LinkSafeClientSetup` is a one-time setup screen for:

- entering the server URL
- testing `/api/health`
- registering the client
- enabling or disabling the agent
- triggering an immediate backup
- opening the client logs folder

`LinkSafeClientAgent` runs silently and is designed to be launched via Scheduled Task at user logon so it can access the current user profile, including:

- `%USERPROFILE%\Desktop`
- `%USERPROFILE%\Documents`
- `%USERPROFILE%\Downloads`
- `%APPDATA%`

Client configuration is stored at `C:\ProgramData\LinkSafeAgent\client-agent-config.json`.

Client state is stored at:

- `C:\ProgramData\LinkSafeAgent\status.json`
- `C:\ProgramData\LinkSafeAgent\queue.json`
- `C:\ProgramData\LinkSafeAgent\Queue`
- `C:\ProgramData\LinkSafeAgent\Logs\backup-agent.log`

## Client backup flow

The agent performs:

1. registration and policy sync
2. scan at every startup/logon, uploading only new or modified files after the initial backup
3. file watching for selected folders
4. debounced incremental packaging for changed files
5. local queueing when uploads fail
6. retry of queued packages
7. heartbeat updates back to the server

Every ZIP package includes `metadata.json`.

Startup backup is important because shutdown/logoff events are not reliable enough to be the only safety net. If a machine powers off before a watcher-driven upload completes, the next logon scan catches what was missed. The scan compares file size and last-write time with confirmed backup state; unchanged files are skipped. The first run backs up all eligible files.

There is no recurring interval scan. The legacy `BackupIntervalMinutes` JSON field is retained for compatibility but ignored by the updated agent. File watching, upload retries, and Run Backup Now remain active. Agent restarts also run the startup scan. Existing clients need the updated agent executable; changing server settings alone does not upgrade an installed agent.

## Additional client drives and folders

Open **Clients > client details > Additional Drives** to select detected local fixed/removable drives, or enter local folders such as `D:\Projects` and `E:\Accounts` (one per line). The agent checks drive availability every 15 seconds independently of backup scans, uploads, and restore prompts. Changed drive lists are reported immediately on the next check; failed reports are retried, and unchanged lists are refreshed every two minutes. Refresh the client details page to see the latest report. Network shares and relative paths are not supported. Unavailable drives are retained in the policy and retried on a later logon/manual scan once connected.

Selections are opt-in. Saving a policy does not immediately scan the entire drive: use **Run Backup Now** or the next user logon. New changes in selected folders are watched after policy refresh. Existing clients need the updated agent EXE; server settings do not upgrade old binaries.

`D:\Projects\file.txt` is stored as `{username}\{computer}\Drives\D\Projects\file.txt`. Identical folder names on different drives stay separate. Known profile folder mappings take precedence when selections overlap. Windows/Program Files, recycle/system-volume folders, paging files, LinkSafe ProgramData, the running agent directory, and the server-configured backup storage path are excluded. Cache/exclusion and file-size rules still apply. This is file backup, not a bootable disk image.

Automatic roaming restore remains limited to known profile folders. Additional drive data stays on the server for administrator-directed recovery; it is not automatically written onto matching drive letters of another device.

## Versioning behavior

When versioning is enabled, the server keeps the old file before overwrite under:

`D:\LinkSafeBackups\{username}\{computerName}\_Versions\yyyy-MM-dd\`

This MVP does not delete stored backups when a user deletes the local source file.

## Install the server

From PowerShell:

```powershell
.\Scripts\Install-LinkSafeServer.ps1 -OpenFirewall
```

This script:

- publishes the server app
- creates `C:\ProgramData\LinkSafeServer`
- creates a Windows service named `LinkSafeServer`
- optionally opens inbound TCP port `5055`

To uninstall:

```powershell
.\Scripts\Uninstall-LinkSafeServer.ps1
```

Use `-RemoveConfig` only if you want to remove `C:\ProgramData\LinkSafeServer`. Backups are intentionally left alone.

## Install the client agent

From PowerShell on the client:

```powershell
.\Scripts\Install-LinkSafeClientAgent.ps1
```

This script:

- publishes the client agent
- creates `C:\ProgramData\LinkSafeAgent`
- registers a scheduled task named `LinkSafe Client Agent`
- starts it at user logon

To uninstall:

```powershell
.\Scripts\Uninstall-LinkSafeClientAgent.ps1
```

To trigger an immediate manual backup:

```powershell
.\Scripts\Run-LinkSafeBackupNow.ps1
```

## Operating the system

Server admins should:

1. install the server
2. complete first setup
3. set the backup root
4. choose the backup folders and limits
5. apply secure storage permissions
6. give the server URL to client setup

Client setup users should:

1. open `LinkSafeClientSetup`
2. enter the server URL
3. test the connection
4. register the client
5. enable the agent
6. run the first backup

## Logs and troubleshooting

Check server logs:

- `C:\ProgramData\LinkSafeServer\Logs\backup-server.log`
- Web UI `Logs` page

Check client logs:

- `C:\ProgramData\LinkSafeAgent\Logs\backup-agent.log`

Common issues:

- connection errors: verify server URL, firewall, and that the web app is running
- permission errors: run storage permission application again and confirm the service account can write to the backup root
- locked files: the agent skips unreadable files and retries later through full backups or later change uploads
- server offline: packages stay in `C:\ProgramData\LinkSafeAgent\Queue` until a retry succeeds

## Workgroup and domain notes

The domain-qualified username is recorded, but LinkSafe sanitizes folder names before storage. For example:

- `OSOOLCAPITAL\mhesham` -> `mhesham`
- `AzureAD\user@company.com` -> `user_company.com`

The server keeps a mapping file at `C:\ProgramData\LinkSafeServer\user-folder-map.json`.

## MVP notes

This MVP uses lightweight JSON storage instead of SQL, is designed for internal LAN usage, and accepts HTTP for initial deployment. HTTPS should be enabled before broader deployment, especially beyond a tightly controlled internal environment.
# Server Setup 1.0.17: Existing Share Paths and Background Hosting

If an older installation saved its own backup folder as a UNC share (for example,
`\\SERVER\UsersBackup$`), Setup 1.0.17 verifies the host against this computer's
name/IP addresses and reads the share's actual local `Path` from Windows. It
validates that path as dedicated, fixed NTFS storage, then updates the backup
root, derived system paths and matching client restore destinations. Backup files
are not moved or deleted. A failed service installation attempts to restore the
original config/registry snapshots and previous binaries. Other-server shares,
unverifiable aliases, clustered shares, missing folders and unsafe paths are
still rejected. Fresh installations still require a new or empty local folder.

Run the new `artifacts/ServerSetup/LinkSafeServerSetup.exe` **on the server**, as
administrator, and choose **Install / update**. Updates use the saved location,
not the new-install folder field. If verification fails, inspect `Get-SmbShare`
on the server and resolve the storage configuration; do not delete backups or
give normal users access to the backup share. This installer update bundles the
unchanged 1.0.16 server and client applications.

Successful installation creates/starts the `LinkSafeServer` Windows service,
sets Automatic startup on both new installs and updates, and configures restart
recovery. Closing Setup or the admin browser does **not** stop that service.
It also runs without an interactive user login. Verify in `services.msc` that
**LinkSafe Server** is **Running** and **Automatic**. A manually launched server
EXE/console is different: closing it can stop the server. If it occupies port
5055, finish active backup uploads and close that manually launched instance
before installing the service; Setup never kills an unowned listener.

Regression checks: `Tests/ServerSetupChecks/StorageUpgradeChecks.ps1` exercises
local-share resolution, remote/unsafe path rejection, configuration preservation,
client destination remapping, JSON arrays and rollback snapshots using fixtures.
It does not install a service, change a share/ACL, or touch live backup data.
