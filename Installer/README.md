# LinkSafe Server Setup

Current package: **server 1.0.20 / installer 1.0.21**, bundling **client setup 1.0.18 / agent 1.0.17**.
See the [root README](../README.md) for current behavior, security limitations and developer handoff.

## Recover from older setup errors

Current Setup retains the Windows PowerShell 5.1 `File.Replace` null-string fix and correctly handles empty/single/multiple-client JSON arrays. Upgrades can resolve an existing same-server share to its verified local NTFS target and rebase registry paths without moving backup files. Remote shares and unsafe destinations remain rejected. Do not uninstall or delete backup/config directories to recover.

Close the old setup, transfer the new EXE to the destination server, run it as administrator and choose **Install / update** first. This replaces the installed helper and starts the service even if the failed LAN step left it stopped. Existing admin credentials, backup location and configuration are retained. Then use **Create admin account** to open the Web UI (sign in if the account already exists), and **Enable LAN access**.

## Install on a new device

1. Transfer `artifacts/ServerSetup/LinkSafeServerSetup.exe` to a Windows x64 server with Desktop Experience. Use a clean Windows Server 2022/2025 machine for deployment testing. A separate .NET installation is not required.
2. Run the EXE as administrator. This build is unsigned, so Windows may display an unknown-publisher warning. Verify the supplied SHA-256 and follow your organization's software approval policy; do not disable endpoint protection.
3. Choose a **new or empty folder on a fixed NTFS drive**, such as `D:\LinkSafeBackups`, and a port (default `5055`). The server application installs under `%ProgramFiles%\LinkSafe Server`.
4. Click **Install server**. Setup creates a LocalSystem Windows service named `LinkSafeServer`, with automatic startup and restart-on-failure settings. Storage and newly created configuration folders receive SYSTEM/Administrators-only ACLs. No SMB share is created.
5. Click **Create admin account**. On this server, the browser opens `http://127.0.0.1:5055/`. Create the admin credentials and sign in. The service initially listens only on loopback, so another LAN device cannot claim the first admin account.
6. Return to Setup and click **Enable LAN access**. This requires an initialized admin account. It changes the listener to `0.0.0.0` and adds a firewall exception for the local subnet on Domain/Private profiles only. It does not change the network's profile.
7. Use the IP printed in the setup log, for example `http://10.0.0.150:5055/`, from other devices. Assign a static IP or DHCP reservation to the server. `0.0.0.0` is a listener address, not an address to enter on clients.
8. In the Web UI, select backup settings. Open **Downloads**, enter this new server's IP, generate the client setup EXE, and install it on the client PCs. Old client installers still point at the old server and are not automatically redirected.

Keep setup open until step 6, or reopen the same EXE later to access the admin and LAN buttons.
Opening setup alone does not install or stop anything.

## What the package contains

- Fresh Release builds of the server, silent client agent and client setup app, with their Windows x64 runtimes.
- Published Web UI static assets and Windows-service integration.
- A manifest with SHA-256 hashes, checked before installation.
- An elevated installation helper, also installed at `%ProgramFiles%\LinkSafe Server\SetupTools\Manage-LinkSafeServer.ps1`.

There are **no existing usernames, admin passwords, registration records, logs, queued packages or backups** bundled. Server runtime state stays in `C:\ProgramData\LinkSafeServer`. The installer does not migrate data from an old server or combine backup histories. Keep the old server and backups until migration and restore tests are complete.

## Updates and uninstall

Rerun this installer to update an installation created by it. Existing configuration, backup location, port and data are retained; incoming form values do not override them. The server is stopped briefly to replace binaries. If service startup fails, previous binaries are restored. Do not update during an active upload or restore. HTTPS/custom service installations require a manual upgrade; the installer refuses to replace a service it does not own.

Uninstall **LinkSafe Server** in Windows Installed Apps/Programs and Features with administrator privileges, or run from an elevated PowerShell:

```powershell
& "$env:ProgramFiles\LinkSafe Server\SetupTools\Manage-LinkSafeServer.ps1" -Mode Uninstall
```

Uninstall removes the service, its installer-created firewall rule, and server binaries. **All backups, config, client download templates and logs remain.** The small SetupTools folder and ownership marker remain to support reinstallation. There is no erase-backups option.

To enable LAN access later from elevated PowerShell:

```powershell
& "$env:ProgramFiles\LinkSafe Server\SetupTools\Manage-LinkSafeServer.ps1" -Mode EnableLan
```

## Troubleshooting and boundaries

- A used port is rejected before installation. On updates, the port already owned by this server service is accepted.
- Existing unrelated installations, non-empty first-install backup folders, network storage, junctions and system/profile folders are rejected rather than overwritten or permission-changed.
- Setup errors appear in the installer details panel. Server logs are under `C:\ProgramData\LinkSafeServer\Logs`.
- Setup respects Windows PowerShell execution policy. If unsigned local scripts are forbidden, installation is blocked; ask your administrator to approve/sign the helper as required by your organization. Setup does not weaken or bypass that policy.
- If LAN access fails, verify the service is running, the correct IP/port is used, and the network is Domain/Private. The default firewall rule does not grant access from routed subnets or public networks; a network administrator must explicitly scope any additional access.
- HTTP exposes traffic to the network. Use only an isolated, trusted LAN for this MVP. The current client API identity/authentication model and previously identified backend regression issues need further hardening before production or untrusted-network deployment. A packaged EXE does not make this a production security certification.
- Windows service installation, reboot startup, firewall access from another PC, and complete backup/restore must still be acceptance-tested on a clean destination machine. Building and validating the package does not replace those tests.

## Build and safe verification

Build from the repository root with a .NET 10 SDK and PowerShell 7:

```powershell
pwsh -NoProfile -File Scripts/Build-LinkSafeServerSetup.ps1
dotnet run --project Tests/ServerSetupChecks/ServerSetupChecks.csproj -- artifacts/ServerSetup/setup-preview.png
powershell -NoProfile -File Tests/ServerSetupChecks/Validate-Setup.ps1
```

Each build uses a fresh staging folder under `artifacts/server-installer`. These local build intermediates are not deployment files. The output EXE is a single file; its internal ZIP is an implementation detail, not a download the user needs to extract.
The validation tests do not install a service or change the live server, firewall, backup data or agent task.
