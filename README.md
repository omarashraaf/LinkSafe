# LinkSafe

Windows user-data backup with an ASP.NET Core admin website/API, a silent per-user agent, and a WinForms client. Clients upload to the server; they do not write directly to an SMB backup share. This README is the current operating guide and handoff for another developer or coding agent.

**Current source versions:** server **1.0.20**, server installer **1.0.21**, client setup **1.0.18** and background agent **1.0.17**. Projects target **.NET 10** and Windows x64 (Shared targets `net10.0`). A built EXE, installed service and installed client can have different versions. Building or pushing Git does not deploy them.

> **Trusted-LAN MVP, not production-hardened.** The admin website requires cookie login, but client controllers currently trust claimed account/device identities and do not enforce the originally requested registration token. This includes restore access. NTFS protection does not fix API impersonation. Do not expose this service to the Internet or an untrusted LAN. Authenticated enrollment, device/user authorization and HTTPS are priority work before production use.

## Start Here

- Operators: read [Server Setup Guide](Installer/README.md), then installation, backup and restore below.
- Domain rollout: [GPO deployment guide](docs/GPO-Deployment.md) covers the unattended kit, read-only software share, user logon GPO, pilot checks and one-time default folders + non-C fixed drives selection. `Scripts/Build-LinkSafeGpoPackage.ps1` now requires signing-certificate/timestamp parameters for distribution; unsigned output needs the explicit development switch. It signs both EXEs and the script before hashing into a new versioned output folder. The server installer/Downloads templates are not automatically signed or updated. Signing is not antivirus approval; the TRADE02 detection still needs vendor review and a clean pilot.
- Coding agents: read [AGENTS.md](AGENTS.md), this README, relevant source and corresponding tests before editing.
- Current antivirus investigation: [September 14 review](docs/Antivirus-Review-20260914.md) identifies the signed tray UI as the detected process and records local integrity/startup fixes. The exact detection trigger and antivirus acceptance remain unverified; do not treat the source fixes as a cleared release.
- Historical context: [development/release notes](docs/HISTORY.md) and dated test READMEs explain regressions; they are not the current deployment contract.

**2026-09-14 deployment status: PAUSED.** At the owner's request, the domain-root link for `LinkSafe - Domain Client Deployment` was disabled and independently read back as `Enabled: False` after a new reported Bitdefender block on TRADE03. The GPO, signed kit (setup 1.0.17 / worker 1.0.16), read-only software share `\\OCDC01\LinkSafeDeploy$`, installed agents and backups were retained. Clients may retain cached deployment policy until refresh. Do not re-enable rollout or repeatedly reinstall the blocked package while investigation is open. Signing and the earlier single-user successful backup pilot did not establish antivirus acceptance across devices. No antivirus settings, backup-storage ACLs or Downloads templates were changed. See the GPO guide's pause record for evidence, audit paths and release criteria.

## How It Works

**GPO cleanup (2026-09-14):** only `LinkSafe - Domain Client Deployment` now remains. Required machine certificate trust, user trusted-publisher and AllSigned settings were consolidated into it before deleting the legacy and pilot GPOs. Domain Computers also has Apply permission so workstation machine trust can process; Domain Users retains Apply and the workstation-only WMI filter remains. The pilot group/filter/share were removed. `\\OCDC01\LinkSafeDeploy$` still serves the same signed kit; its underlying folder's historical `Pilot` name does not mean pilot-only scope. Rollback archives, installed agents and backup data were retained. See the cleanup record in the GPO guide for verification and the empty, locked legacy SYSVOL directory left behind. Endpoint policy refresh and logon verification after consolidation remain outstanding.

```text
Windows user logs in
  -> SID-bound scheduled task starts agent as that user
  -> agent registers and refreshes backup policy
  -> startup scan compares local data/state with server presence
  -> new/changed or server-missing data becomes bounded ZIP uploads
  -> server validates/extracts into BackupRoot/user/computer
  -> confirmed files update client state; failed uploads stay queued

While running: file watching + policy/drive discovery + retry + heartbeat
Admin Back up now: persistent request -> agent polling -> real scan/result
Restore: server manifest -> compare original local destinations
  -> approval as configured -> copy only missing files
```

### Backup Behavior

- Login/startup scanning is the safety scan. Normal operation does not run a full scan every old `BackupIntervalMinutes` interval; do not accidentally reintroduce that behavior. Watcher changes, manual requests and newly selected/reconnected sources also trigger work.
- Defaults include Desktop, Documents, Downloads, Pictures, Videos, Music, Favorites, AppData Roaming and Chrome/Edge/Firefox profiles. All AppData Local is not selected by default. Local custom folders and additional drives are supported, subject to protected-path, exclusion and availability checks.
- Folder precedence is **local PC override > per-client server override > global server defaults**. Local overrides replace server folder choices. Global exclusions, limits and timing still apply. Choose **Use server settings** on the client to stop overriding the server.
- Policy/drive refresh is independent of long uploads (about every 15 seconds). Heartbeats and inventory reporting have their own cadence. Selected drives must be ready and readable on that PC.
- Empty directory backup, ZIP timestamp clamping, bounded packages, server-presence repair and upload-confirmed state are implemented. Do not replace presence checks with local timestamp-only checks: deleting server backups must allow them to be rebuilt.
- Locked, unreadable, excluded, oversized or changing files can be skipped/reported. This is not VSS imaging; file counts alone do not prove a complete profile backup. Inspect errors and verify representative files.
- **Back up now / Stop backup** is one stateful action. Stop cancels work and persists a pause, retaining packages. Resume with Back up now. **Enable / Disable agent** controls the agent separately. Queued requests, old success timestamps and enabled preferences do not prove newly completed backups.
- No `/MIR`, delete synchronization or automatic deletion of server files when local files disappear. Old copies are versioned before replacement when versioning is enabled.

### Admin Website

Navigation: Dashboard, Clients, Settings, Downloads and Logs. Legacy Browse/Jobs page files remain in source but are not navigation tabs. Backup job records still support dashboard/status history.

Clients supports individual actions and bulk selection (up to 200 PCs): backup requests, folder/path settings, restore server defaults, enable/disable server access and registry removal. Bulk actions require review/confirmation and CSRF protection. Removing a record does not delete backups or uninstall the agent; running agents can register again.

**Live status (1.0.20):** Clients, Dashboard and Logs poll authenticated GET pages every five seconds without reloading. Filters, selections and unsaved bulk edits remain intact. Hidden tabs pause; failures show stale-data/session warnings and retry with backoff. Storage usage is cached and measured asynchronously about once a minute. Logs preview at most the last 256 KiB / 200 lines. These are latest *reported* values, not direct observation of a remote PC.

**Server-requested backup (agent 1.0.16+):** the server stores a request; an independent agent loop polls about every 15 seconds. Acknowledgment is automatic, not a user approval dialog. Offline/stopped agents cannot execute it. A user-paused/disabled agent reports Blocked when it can check in; the server does not silently resume it. Repeated requests reuse pending work. Preserve Queued/Blocked/Running/terminal distinctions.

### Restore / Move Data

Restore is manual only. Use **Restore data** in the client when you want to compare and recover missing data; startup, login, tray launch and normal app opening never prompt for or start a restore. The move-from-another-device flow lets the same domain-qualified account choose a source computer, with its last backup shown. It does not merge all devices.

The client compares a server manifest with original destination paths, previews missing/existing counts, then copies **only missing files**. Existing files, even different versions, remain untouched. User folders map to the current user's known folders; app/browser files map to corresponding AppData locations. Drive/custom data requires an enabled valid local mapping; missing mappings produce warnings, not guessed destinations. Failed transfers do not leave partial destination files.

This is file migration, not a full roaming Windows profile, application installer or registry clone. Close applications before restoring settings. Passwords, device-bound credentials and some application state are not portable. Empty directory restore and very large manifests remain limited; inspect the restore service/tests before extending them. Source/server data is not deleted by "move". Same short usernames on unrelated workgroup PCs are not necessarily the same domain-qualified identity.

## Install And Update

### Server

1. Build or obtain a trusted `LinkSafeServerSetup.exe`. It bundles server, runtime and client templates for Windows x64; no separate .NET installation is required on the target. Current builds are unsigned.
2. Run Setup as administrator on Windows Server with Desktop Experience. For first install choose a new/empty folder on a fixed local NTFS drive, for example `D:\LinkSafeBackups`, and port `5055`.
3. Install/update, open `http://127.0.0.1:5055/` on that server and create the admin account. Setup initially binds to loopback to prevent remote first-account claiming.
4. Use **Enable LAN access** in Setup. Then use `http://SERVER-IP:5055/` from other PCs. The installer firewall rule covers local-subnet Domain/Private networks, not public networks.
5. Configure folders, timing and storage in Settings, review storage permissions, then distribute clients through Downloads. Never grant normal users access to the backup root.

The `LinkSafeServer` Windows service runs automatically in the background after successful installation. Closing Setup or the browser does not stop it. Running a development process is not a service installation.

Updates preserve config, credentials and backups. Update during an agreed quiet window: the service stops briefly for binary replacement. Existing same-server SMB backup paths can be resolved to their verified local share target during upgrade; remote shares, unsafe paths and unrelated installations are rejected. No backup files are moved by this conversion. See the installer guide and storage-upgrade tests.

### Client

1. In server **Downloads**, enter only the reachable server IP and generate the single setup EXE. Port comes from server config. Keep the generated filename: current bootstrap derives the address from it, rather than embedding a new config resource in the binary.
2. Run it as the actual backup user, not under a different administrator account. Test connectivity and **Enable agent**. Registration/policy download happen in the background; no token entry is required in the current client.
3. Enable installs the managed UI, worker and this user's login integration. The UI appears in Start/Installed Apps and closes to the tray. UI tray startup and the background scheduled task are separate mechanisms.
4. Choose folders locally only when this PC should override server selections. Verify the effective folders, startup state and a successful test backup.

The server retains its two template binaries; generated downloads are cleaned up on generation/download on a best-effort basis. Locked files can prevent cleanup. Do not delete templates to remove old generated downloads.

To update clients, install the matching server package/templates, generate a fresh setup, close the old tray UI and run the new setup as the same backup user. Managed upgrades check replacement metadata and use bounded rollback/cleanup; do not delete arbitrary older EXEs across the drive. Updating the server does not prove all agents upgraded. Live UI 1.0.20 needs no newer agent than 1.0.16.

### Uninstall

Use Windows Installed Apps for **LinkSafe Server** (administrator) or **LinkSafe Client** (owning user). Server uninstall keeps backup/config/log data; client uninstall keeps retained state/queue/logs by default. Do not delete these to repair startup. Legacy protected tasks may require an administrator to run `Scripts/Repair-LinkSafeLegacyTask.ps1` for the exact owning account; use `-WhatIf` first. Never grant Everyone permissions or run backups as SYSTEM to bypass a client permission error.

## Storage And Diagnostics

| Location | Purpose |
| --- | --- |
| `C:\ProgramData\LinkSafeServer` | Config, registered clients, jobs, identity mapping, remote requests and protected keys |
| `C:\ProgramData\LinkSafeServer\Logs\backup-server.log` | Server log; also previewed in admin Logs |
| `C:\ProgramData\LinkSafeServer\Downloads` | Installed client templates and temporary generated setup |
| `%ProgramFiles%\LinkSafe Server` | Installed server binaries and SetupTools |
| `%LOCALAPPDATA%\LinkSafeAgent` | Current user's config, policies, backup state, status, signals, queue and restore staging |
| `%LOCALAPPDATA%\LinkSafeAgent\Logs\backup-agent.log` | Current user's agent log |
| `%LOCALAPPDATA%\LinkSafeAgent\bin` | Managed background agent executable |
| `%LOCALAPPDATA%\Programs\LinkSafeClient` | Installed UI executable |
| `C:\ProgramData\LinkSafeAgent` | Retained legacy data; not the current writable workspace |

```text
D:\LinkSafeBackups\
  _Incoming\          temporary server uploads
  _Quarantine\        rejected/suspicious packages
  _Versions\          system versioning directory
  _Logs\              storage system directory (main log is in ProgramData)
  user\computer\
    Desktop\ Documents\ Downloads\ AppDataRoaming\ ...
    Drives\ ...       additional sources use logical package mappings
    _Versions\date\   prior file copies, preserving relative paths
```

Storage ACLs are for SYSTEM, Administrators, the server identity and an optional approved backup-admin group, not normal users. No client SMB writes and no share creation by default. Never loosen server ACLs to fix a client configuration-directory error: client state belongs in that user's LocalAppData.

For connection failures check service, actual IP/port, firewall and server log. For stale folders check local/per-client overrides, policy sync and drive readiness. For stalled backups inspect request ID, stop signal, queue/disk space and worker log; do not delete queue/state to hide errors. For missing restored files check source, manifest, exclusions, original-path mapping and preserved existing files. Files deleted on the server should be repaired by presence-aware scans, not by blindly clearing client history.

## Developer Map

| Area | Entry points |
| --- | --- |
| Server host/auth/static files | `LinkSafe/LinkSafeServer/Program.cs` |
| Website/forms/bulk actions | Server `Pages`, `Models/BulkClientSelection.cs` |
| Live website | Server `wwwroot/js/live.js`, `wwwroot/js/site.js`, `Services/DashboardService.cs`, `Services/LogReaderService.cs` |
| Registration/policy/commands | Server `Controllers`, `Services/ClientRegistryService.cs`, `Services/RemoteBackupService.cs` |
| Upload/repair/versioning | Server `Services/BackupStorageService.cs`, `BackupPresenceService.cs`, `StorageSecurityService.cs` |
| Server restore | Server `Services/RoamingRestoreService.cs`, `Controllers/RestoreController.cs` |
| Agent orchestration | Agent `Program.cs`, `Worker.cs`, `Services/BackupCoordinator.cs` |
| Packaging/watchers/policy | Agent `Services/BackupPackageBuilder.cs`, `FolderMappingService.cs`, `DriveInventoryWorker.cs` |
| Remote polling | Agent `Services/RemoteBackupWorker.cs`, `RemoteBackupInbox.cs` |
| Client UI | Setup `MainForm.*.cs` partial files |
| Client install/upgrade | Setup `ClientDesktopInstallation.cs`, `LegacyClientMigration.cs`, `MainForm.Installation.cs`, `MainForm.Upgrade.cs` |
| Shared contracts/state/path safety | `LinkSafe/Shared/Models`, `Storage`, `Constants/LinkSafeDefaults.cs` |
| Single EXE installer | `Installer/LinkSafeServerSetup`, `Scripts/Build-LinkSafeServerSetup.ps1`, `Scripts/ServerSetup/Manage-LinkSafeServer.ps1` |

Server base: `LinkSafe/LinkSafeServer`. Agent base: `LinkSafe/LinkSafeClient/LinkSafeClientAgent`. Setup base: `LinkSafe/LinkSafeClient/LinkSafeClientSetup`.

JSON is the MVP persistence layer, not SQL. Use existing atomic writes and mutation locks; a read-modify-write sequence is not protected merely by locking individual reads/writes. UI and worker are separate processes sharing per-user state. Preserve contract compatibility, operation IDs and cancellation semantics.

## Build And Test

Run from repository root on Windows with .NET 10 SDK and PowerShell 7. Building needs NuGet access. Read external API documentation through Context7 before framework/package changes.

```powershell
dotnet restore LinkSafe.slnx
dotnet build LinkSafe.slnx -c Release -p:UseSharedCompilation=false
# Several suites load this exact non-RID Release server output:
dotnet build LinkSafe/LinkSafeServer/LinkSafeServer.csproj -c Release -p:UseSharedCompilation=false
dotnet run --project Tests/RemoteBackupChecks/RemoteBackupChecks.csproj -c Release -p:UseSharedCompilation=false
pwsh -NoProfile -File Tests/ServerSetupChecks/StorageUpgradeChecks.ps1
```

These are executable harnesses, not conventional `dotnet test` projects. Installer/tests are outside `LinkSafe.slnx`; run them explicitly. Other targeted suites under `Tests/` cover BackupFlow, BackupRepair, BackupStop, RestoreFlow, PolicySync, Startup, ClientWorkspace, ClientInstallation, ClientUpgrade and ClientUi. Read runners before execution; optional live/installed checks are not isolated fixtures. The dated ReviewChecks report is historical, not proof every finding remains or is fixed.

Repository handoff verification (2026-09-11): a clean export of staged source built the Release solution with zero warnings/errors, then passed all 74 RemoteBackupChecks and 72 storage-upgrade checks. These tests did not modify live services, clients or backups. This is a scoped result, not a claim that every suite or deployment scenario was retested.

Build the redistributable:

```powershell
pwsh -NoProfile -File Scripts/Build-LinkSafeServerSetup.ps1
```

Output: `artifacts/ServerSetup/LinkSafeServerSetup.exe` and `.exe.sha256`. The script freshly publishes server/client runtimes and static assets, creates a SHA-256 manifest and embeds the payload. Never package live ProgramData, credentials, records or backups. `bin`, `obj`, `publish` and `artifacts` are generated locally and excluded from Git; a fresh clone must build them.

Verify the exact `payload.zip` from that build's GUID staging directory:

```powershell
dotnet run --project Tests/ServerSetupChecks/ServerSetupChecks.csproj -c Release -p:PayloadPath="ABSOLUTE-PATH-TO-payload.zip" -- artifacts/ServerSetup/setup-preview.png --verify-payload
```

If publishing fails with a user-mapped reference DLL lock, stop only your own fixture/build processes and try `dotnet build-server shutdown`. Session-scoped `ProduceReferenceAssembly=false` and `UseSharedCompilation=false` were used for the 1.0.20 build; this disables compiler reference outputs, not application binaries. Do not kill installed agents/services or delete user state to unlock a build.

Do not casually run the real server/agent for a development smoke test: default entry points use real ProgramData/LocalAppData. Prefer isolated hosts/stores or an approved disposable VM. Packaging tests do not prove service startup, firewall reachability, logon/tray startup or full backup/restore on another device. Acceptance-test those before rollout. For CLI semantics see [Microsoft publish documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish).

## Handoff And Next Work

First inspect `git status`, current versions, relevant implementation and tests. The user chose missing-only restore, explicit source-device choice, local folder overrides, persistent Stop backup, login scanning and a compact teal UI. Preserve these decisions unless asked otherwise.

- Priority: authenticate enrollment and bind backup/restore/policy/command operations to verified identities. A shared token or filtered dropdown alone is not authorization. Add HTTPS and protect bootstrap/download trust before broader deployment.
- Test clean installation and upgrades with standard domain users, multiple users per PC, Windows PowerShell 5.1, logon/offline recovery, disk exhaustion and inaccessible sources. Do not claim production readiness from fixture tests.
- Review queue disk-pressure/retention and large restores before scaling. JSON registries, file-by-file manifests and unsigned binaries are MVP constraints.
- Preserve ZIP containment, reparse-point rejection, NTFS ownership, missing-only atomic restore and upload-confirmed state. Add regression tests for repaired failures.
- Retain installer fixes: PowerShell 5.1 `File.Replace` null-string handling; empty/single/multiple JSON client arrays; verified same-server UNC-to-local migration; config/registry/binary rollback without moving backups.

Ask the owner only for consequential ambiguity: overwrite/delete semantics, permission changes, production rollout, different account/device targets or changing paused-agent behavior. Otherwise inspect, make a minimal compatible change and test it. Report what changed, checks run, remaining gaps, EXE versions built and whether anything was actually deployed. Never say the installed app was updated just because Git was pushed.
