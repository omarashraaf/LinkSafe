# LinkSafe Coding-Agent Instructions

Read README.md first for current behavior, paths, architecture, build/test commands and security limits. docs/HISTORY.md and dated test notes are historical. Inspect current code before acting on an old bug report.

- Always use Context7 for framework/library API documentation, setup/configuration and package-dependent code generation. Resolve the library and prefer version-specific docs before writing that code.
- Preserve existing uncommitted work. Use apply_patch for manual edits. Never reset, broadly delete or overwrite another contributor's changes.
- Never use real user data, backup roots, live registry entries or production accounts as test fixtures. Default app entry points use real state. Prefer isolated stores/test hosts or a disposable VM.
- Building, committing and pushing are not deployment. Ask before installing/restarting a live service, replacing remote downloads, dispatching real backups/restores, resuming paused agents or changing startup/ACLs.
- Do not grant normal users access to server storage, add client SMB writes, bypass execution/security policy, or run user backups as another account/SYSTEM.
- Preserve missing-only restore, explicit source-device choice, local folder override precedence, persistent Stop backup and login scanning unless the owner requests different semantics.
- Keep auth limitations explicit. Client API identity is currently claimed, not securely authenticated; web cookie authorization does not secure controllers.
- Keep queue/state writes atomic and honor operation IDs/cancellation. Do not report queued/no-change/partial work as newly completed success.
- Add focused regression checks and run relevant suites. Harnesses are console projects invoked with dotnet run; several need a non-RID Release server build first. Inspect runners before optional installed/live flags.
- Exclude build outputs, EXEs, archives, credentials, runtime JSON, logs and real backups from Git. Keep source, scripts, tests, assets and documentation reproducible. Do not remove local outputs merely to clean git status.
- Verify server, installer, client-setup and worker versions separately. Rebuild with Scripts/Build-LinkSafeServerSetup.ps1; never use an arbitrary stale publish folder as payload.
- Finish with changes, actual test results, limitations and exact deployment state. Ask concise questions only for consequential ambiguity or live/destructive operations.
