# LinkSafe

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
2. full backup at startup/logon
3. file watching for selected folders
4. debounced incremental packaging for changed files
5. local queueing when uploads fail
6. retry of queued packages
7. heartbeat updates back to the server

Every ZIP package includes `metadata.json`.

Startup backup is important because shutdown/logoff events are not reliable enough to be the only safety net. If a machine powers off before a watcher-driven upload completes, the next startup full backup catches what was missed.

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
