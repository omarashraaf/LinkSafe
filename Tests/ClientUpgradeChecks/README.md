# Managed updates and device migration (1.0.7)

## Agent update behavior

Opening the client checks an existing agent in `C:\ProgramData\LinkSafeAgent\bin` against the version required by that client release. Enable agent and Back up now also perform this check. This is not a scheduled internet updater: install the updated server download template and open the newer client to trigger an upgrade.

The updater downloads a candidate before touching the installed executable, checks its LinkSafe original filename and version, and never downgrades. With a running 1.0.6+ agent it requests a cooperative backup pause, waits for acknowledgement, then stops only the exact managed executable in the current Windows session. Instances outside that path or in another session are not stopped/deleted. Pre-1.0.6 running agents cannot acknowledge a safe pause and need a one-time exit/sign-out before replacement.

Replacement keeps a rollback copy until activation returns. A failed activation stops the attempted process and restores the previous executable. A running/enabled agent is restarted automatically. A pause created for the upgrade is cleared with a new full-scan request; a pre-existing user pause is preserved. The setup owns an exclusive update lock and blocks its own backup/migration actions during upgrade.

Only the previous canonical binary and older, recognizable `LinkSafeClientAgent.<guid>.previous.exe` rollback copies inside the managed bin folder are deleted. There is no drive-wide search or deletion of downloads, arbitrary EXEs, other users' agents, config, logs, queued ZIPs or backups. Locked-file/access failures are reported instead of deleting more broadly. Rollback is best-effort if OS permissions or process termination prevent recovery. The download checks are metadata checks, not a digital signature or trust proof; the existing trusted-LAN/unsigned MVP restrictions still apply.

## Move data to this device

Use this option on the destination PC under the same domain-qualified Windows account. Choose a different source device, compare its server backup against current local paths, review the source/destination preview, and approve copying only missing files. A new client with no previous restore still receives the existing startup offer; the explicit button allows choosing the source again at any time.

The files go inside the current Windows user's known Documents, Desktop, Downloads and other supported folder paths. AppData Roaming and browser-profile files map to the current user's AppData directories. They are not extracted under a new device-name folder or delivered as a ZIP. Missing-only behavior and atomic downloads are shared with the tested restore flow; existing local files and all server/source backups are kept.

Only data actually backed up can be transferred. Close affected applications first. Missing file-based settings can be copied, but this is not an application installer or Windows-profile/registry clone; passwords and device-bound credentials may not work on another PC. Existing destination app configuration is kept as requested. Additional-drive/custom paths require a valid enabled destination mapping; unavailable mappings produce warnings, not guessed paths. Same short usernames on unrelated workgroup PCs are deliberately not treated as the same domain-qualified identity.

The current server still trusts a claimed client identity; this feature does not repair the documented API authentication limitations. Keep the MVP restricted to a trusted isolated LAN. Do not treat this UI device filter as an authorization boundary.

## Tests and deployment

`dotnet run --project Tests/ClientUpgradeChecks/ClientUpgradeChecks.csproj -c Release` uses fixture files with an injected version reader and simulated activation callbacks. It verifies replacement, rollback, locked destination, invalid/stale downloads, bounded cleanup, preserved config/queue, device filtering, known-folder destinations, app/browser mappings, existing-file preservation and preview text. No actual process is stopped and no live profile/config/backup is modified. ClientUiChecks covers the new action and update-operation guard; RestoreFlowChecks covers actual missing-file transfer through fixture HTTP services.

Update the server with installer 1.0.7, generate/download the new client, and exit the old tray UI before opening the new one. Existing 1.0.6 agents can then update automatically without the old manual disable/enable sequence. Test startup, rollback/recovery and migration on a disposable/affected PC before wider deployment.
