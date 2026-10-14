# Antivirus Review: September 14, 2026

## Confirmed Incident

Read-only inspection of TRADE03's deployment/agent logs and Bitdefender incident JSON established the following, in the endpoint's displayed UTC-07 time:

| Time | Evidence |
| --- | --- |
| 04:58:45 | Worker started, registered and downloaded policy. |
| 04:58:49 | GPO setup returned installation complete with initial backup requested. |
| 04:59:13 | Worker created a package containing 1,629 files and 938 directories, then began uploading it. This is not proof of upload completion. |
| 04:59:49 | Bitdefender Advanced Threat Defense recorded `SuspiciousBehavior.6E967C221BFCA98E`. |

The incident's `real_trigger_file_path` identifies `%LOCALAPPDATA%\Programs\LinkSafeClient\LinkSafeClientSetup.exe`, running with `--tray`, and its detection name is `ATC.Malicious`. Its parent is the cached setup running `--gpo-install`, launched by PowerShell. A recorded child of the tray UI is `schtasks.exe /Query /TN <this user's LinkSafe task> /XML`. The tray's periodic startup verification in `MainForm.Startup.cs` explains that query; it is not evidence of task creation at that time.

The recorded tray SHA-256 and a fresh read-only hash of the installed UI both match the preserved signed distribution candidate:

`E82C2F36450231D8B9D83AA222916B25F7915EB31598558F077AFBF5A299BC2A`

The installed worker hash also matches its preserved distribution candidate:

`1D423EEFC3C7CA6BC96C9F382A13033BC9B04D08F24A95A0CDB45F511B8DE6B3`

The vendor record includes the internal signing certificate information. Its behavior classifications are not a detailed trace proving a specific source API injected into another process or caused the score. The reviewed setup/shared source does not contain calls to `WriteProcessMemory`, `VirtualAllocEx` or `CreateRemoteThread`; this limited source search is not proof of complete runtime safety. The record's numeric internal fields are not interpreted as vendor clearance.

**Conclusion:** installation ran and a backup began, but the signed tray process was subsequently classified as malicious. The available record identifies the process, not the exact triggering operation. It does not establish that the upload, PowerShell launcher, task query, browser files or certificate alone caused detection. Earlier installer-only speculation is superseded. No verified antivirus remedy has been established.

## Concrete Code Findings And Fixes

1. GPO deployment previously verified the package worker's hash but could reuse an installed worker/UI based on version metadata alone. It now verifies reused worker bytes against the package hash and reused UI bytes against the running distribution setup. Both are checked before launching installed applications. A different installed release stops with an actionable error; it is not deleted, downgraded or silently replaced. This narrows GPO reuse compatibility intentionally and requires an approved upgrade for a different release. A hash anchored to the distribution package does not replace trusted package distribution, code signing or a secure update protocol, and is not a complete defense against same-user file races.
2. `AgentStartupTask.InstallAsync` previously recreated an already-correct task. It now returns after full validation, making repeated enable/install operations read-only for healthy tasks. Validation additionally requires exactly one principal and one user-logon trigger so the optimization cannot preserve an extra trigger/principal accidentally. An owned task with incorrect settings still follows the existing repair/readback path; foreign tasks still fail closed.

These are independently testable integrity/idempotence fixes. Neither is represented as the proven cause of the Bitdefender incident. Existing metadata-only non-GPO HTTP update validation and the README's unauthenticated client API limits remain outside these fixes and remain security work.

## Verification And Deployment State

- 47 isolated GPO deployment checks passed, including identical/different/missing installed payloads, no destructive replacement on mismatch, progress and retry semantics.
- 33 isolated startup checks passed, including no rewrite of a healthy task, rejection of additional principals/triggers, ownership checks and repair/readback failure handling.
- Release solution build succeeded with zero warnings and zero errors; `git diff --check` passed.
- No live installer, task, backup, restore or agent was executed by these tests. Endpoint evidence collection was read-only and did not read user document contents or ZIP contents.
- Source changes are local only, not a signed/released installer. Preserve the original signed sample for vendor investigation. No remote downloads or deployed EXEs were replaced, and the domain GPO link remains paused.
- Bitdefender remains enabled. No exception, policy bypass, binary renaming/obfuscation, security-product modification or third-party upload was performed.

Before claiming a resolution, obtain vendor analysis of the exact recorded sample and/or an approved isolated test of a correctly signed release with protection enabled. Verify startup, subsequent logon and representative backup completion, not merely installer exit code. Do not reactivate domain rollout on the basis of unit tests alone.
