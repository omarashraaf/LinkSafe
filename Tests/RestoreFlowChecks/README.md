# Missing-only restore (1.0.3)

The server provides a manifest at `GET /api/restore/manifest`. The client compares the manifest against original local destinations before asking for approval. Only absent files are downloaded through `GET /api/restore/file`; existing files are never replaced, even when their contents or timestamps differ. Local-only files are never deleted.

Downloads are written to unique temporary files beside their destinations, size-checked, and moved into place without overwrite. Files created locally after comparison are preserved. Truncated/failed/cancelled downloads do not leave partially restored destination files. Per-file failures are reported and do not stop unrelated files; the next comparison retries only files still absent. The UI displays file/byte progress and a result count, not success on partial failure.

Manual Restore Data can discover the current PC's own backup as well as other PCs for the same claimed account. The previously selected source is preferred, then another device, then this PC. The confirmation identifies the chosen source. This does not merge multiple device snapshots. Startup and login do not discover, prompt for, or run restore; restore starts only from the client app.

User-folder backups go to the current Windows user's known folder paths. Additional-drive paths are restored only when they map to the client's enabled custom/drive source paths. Missing mappings, inaccessible destinations, links/junctions, and protected system/LinkSafe storage paths are reported rather than guessed or silently overwritten. Empty directories are not included in the file manifest. The manifest is limited to 200,000 files; an oversized manifest fails explicitly.

## Update

1. Run server setup 1.0.3 and select Install / update.
2. Generate/download a new client setup from that server. Exit the old tray UI, open the new setup and use Disable Agent then Enable Agent to update the background executable while retaining configuration/queue.
3. Click Restore Data. Check the source and missing/existing counts, then approve. Review the final restored/preserved/error counts.

The new clients deliberately do not fall back to the old overwrite-all ZIP restore. Old server/client binaries must be upgraded together. The original client API authentication/identity limitations are not fixed by this feature: the account in a query is not authenticated identity. Keep this MVP on an isolated trusted LAN; do not expose it to untrusted users or the internet. See the existing review report for production hardening work.

## Verification

Build the server in Release first, then run `dotnet run --project Tests/RestoreFlowChecks/RestoreFlowChecks.csproj -c Release`.
The test injects a fixture registry and uses the real server manifest/file services through an in-memory HTTP handler. It does not contact the running server, invoke a real restore, or touch production profiles, backups or config. Tests cover comparison-only discovery, missing files across user folders and an enabled additional source, preserved existing/local-only files, concurrent creation, truncation, retries, zero-byte files, progress, path traversal, server changes, and cancellation. The symbolic-link fixture is skipped if Windows denies the required privilege; execution/security policies are not changed.

A manual restore on the affected PC still requires acceptance testing after installation.
