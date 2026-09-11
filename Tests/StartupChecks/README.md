# Login startup verification (1.0.9)

## 1.0.10 Windows export regression fix

Windows can export a trigger UserId as DOMAIN\user while preserving the principal as a SID, and omit the default LeastPrivilege element. The 1.0.9 verifier incorrectly rejected that successful registration. The corrected verifier resolves names to SIDs, accepts the omitted default, and retains checks for other users, disabled tasks, unsafe power/runtime settings and unknown identities. Current logged-in account resolution does not require a domain lookup. Ownership checks in repair/removal and the developer installation script also use SID comparison.

The suite now has 29 fixture checks. With `--verify-installed`, it adds a **read-only** query of the actual LinkSafe task: 30 checks passed on the affected development machine. No task registration/update/removal or agent restart is performed by that option. Real post-registration XML was inspected, unlike the earlier validation-only schema check, which could not catch Windows rewriting the task representation.

Client setup 1.0.10 retains the required background agent version 1.0.9: this correction is in the setup/verifier, not the backup engine. Opening the corrected client on a PC already running agent 1.0.9 does not force a binary upgrade. For a direct local update, `artifacts/ClientSetup/LinkSafeClientSetup.exe` reads the saved server address. The rebuilt server installer includes this corrected Downloads template for other PCs.

`dotnet run --project Tests/StartupChecks/StartupChecks.csproj -c Release`

22 checks cover generated XML, explicit user SID, path escaping, battery operation, no execution cutoff, restart settings, invalid/disabled tasks, different-user protection, creation failures and persisted-task readback. The command runner is injected: no real task is created/deleted and no agent runs. Only GUID-scoped fixture files and temporary XML files are written. The XML also passed the installed Windows Task Scheduler's `TASK_VALIDATE_ONLY` check, which does not register a task.

The previous client ignored the exit code from an elevated `schtasks /Create` call and set IsAgentEnabled before installation. The resulting task used scheduler defaults that prohibited battery starts/stopped on battery, lacked restart-on-failure, and did not explicitly bind its logon trigger to the intended user. The new task uses the current user's SID, InteractiveToken, LeastPrivilege, a 15-second login delay, unlimited execution and three one-minute failure retries. Worker failure exits nonzero so scheduler retries can apply. The client now checks actual task XML and shows startup configuration problems separately from the saved enabled flag.

## Deploy and verify

1. Update the server with the 1.0.9 installer; generate a fresh client setup from Downloads.
2. Run the new client as the Windows user whose files should be backed up, and click Enable agent. For an older elevated agent/task, a one-time run as administrator under **the same account** may be needed. The client does not silently install under an alternate administrator account or replace another user's task.
3. Confirm the client shows Startup task verified. If permissions or organization policy deny task registration, resolve that reported error; do not assume the enabled preference means startup is installed.
4. Sign out/in on a test device. Confirm Task Scheduler's LinkSafe Client Agent task runs for that user, and the agent log contains an agent start and logon backup scan. Test with battery power and with the network initially disconnected. The agent has no console/client window at login; the client UI/tray is opened separately.
5. Verify a deliberately created small test file is backed up. A pre-existing Stop backup pause still persists; click Back up now to resume. A pending interactive restore prompt may need an answer before the startup scan proceeds.

No reboot, live task repair, actual affected-PC upgrade, or live backup was initiated during this verification. The locally inspected machine already reported the agent running after its latest restart and a later successful upload, so absence of a visible client window alone is not proof of startup failure.

References: [Microsoft logon task XML](https://learn.microsoft.com/en-us/windows/win32/taskschd/logon-trigger-example--xml-), [validation-only registration](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskfolder-registertask).
