using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientSetup;

public sealed partial class MainForm : Form
{
    private const string DefaultServerUrl = LinkSafeDefaults.DefaultServerUrl;
    private static readonly TimeSpan RestoreDiscoveryPromptTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RestoreDiscoveryBackgroundTimeout = TimeSpan.FromSeconds(5);
    private static readonly string AgentInstallDirectory = Path.Combine(LinkSafeDefaults.ClientDataRoot, "bin");
    private static readonly Regex GeneratedSetupNameRegex =
        new(@"^LinkSafeClientSetup-(?<host>.+)-p(?<port>\d+)\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly JsonFileStore<ClientAgentConfig> _configStore = new(LinkSafeDefaults.ClientConfigPath);
    private readonly JsonFileStore<StatusFileModel> _statusStore = new(LinkSafeDefaults.ClientStatusPath);
    private readonly TextBox _serverUrlTextBox = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _askBeforeRestoreCheckBox = new() { Text = "Ask every time before restoring data", AutoSize = true, Checked = true };
    private readonly Label _statusLabel = new() { Dock = DockStyle.Fill, AutoSize = false, Height = 160 };
    private readonly ProgressBar _progressBar = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Label _progressLabel = new() { Dock = DockStyle.Fill, AutoSize = true, Visible = false };
    private readonly NotifyIcon _notifyIcon = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private bool _hasEmbeddedServerUrl;
    private bool _allowClose;
    private bool _backupRequested;
    private bool _stopRequested;
    private bool _restoreRequested;
    private CancellationTokenSource? _restoreCancellation;

    public MainForm() : this(true) { }

    internal MainForm(bool enableBackgroundIntegration) : this(enableBackgroundIntegration, false, false) { }

    internal MainForm(bool enableBackgroundIntegration, bool startInTray, bool uninstallAtStartup)
    {
        BuildDashboard();
        if (startInTray)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Shown += (_, _) => Hide();
        }
        if (!enableBackgroundIntegration) return;

        ConfigureNotifyIcon();
        Shown += async (_, _) =>
        {
            if (uninstallAtStartup) await UninstallClientAsync();
        };

        _askBeforeRestoreCheckBox.CheckedChanged += async (_, _) => await SaveRestorePreferenceAsync();

        _refreshTimer.Tick += async (_, _) => { try { await RefreshStatusAsync(); } catch { } };
        Load += async (_, _) =>
        {
            await LoadConfigAsync();
            if (uninstallAtStartup) return;
            if (!startInTray) await UpdateInstalledWindowAsync();
            if (!startInTray) await CheckInstalledAgentOnOpenAsync();
            _refreshTimer.Start();
            if (!startInTray) _ = CheckRestoreDataInBackgroundAsync();
        };
        FormClosing += OnFormClosing;
    }

    private Button CreateButton(string text, EventHandler handler)
    {
        var button = new Button
        {
            Text = text, AccessibleName = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink,
            Padding = new Padding(12, 4, 12, 4), Margin = new Padding(0, 0, 10, 0),
            MinimumSize = new Size(154, 40), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Tint;
        button.Click += (sender, args) =>
        {
            if (CanRunAction(button.Text)) handler(sender, args);
        };
        return button;
    }

    private bool CanRunAction(string text) =>
        (!_uninstalling && !_upgrading && !BackupActive && !_restoreRequested && !_stopRequested && _pendingAction is null) ||
        text.Equals("Open logs", StringComparison.OrdinalIgnoreCase) ||
        (text.Equals("Stop backup", StringComparison.OrdinalIgnoreCase) && !_uninstalling && !_upgrading && !_restoreRequested && !_stopRequested && _pendingAction is null);

    private async Task TestServerAsync()
    {
        ShowProgress("Testing connection...");
        try
        {
            var serverUrl = NormalizeServerUrl(_serverUrlTextBox.Text);
            using var client = new HttpClient { BaseAddress = new Uri(serverUrl) };
            var response = await client.GetAsync("api/health");
            response.EnsureSuccessStatusCode();
            _serverUrlTextBox.Text = serverUrl;
            ShowCompleted("Connection successful.");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Connection failed: {ex.Message}");
        }
    }

    private async Task EnableAgentAsync()
    {
        ShowProgress("Enabling agent...");
        try
        {
            var serverUrl = NormalizeServerUrl(_serverUrlTextBox.Text);
            var agentExePath = await EnsureAgentBinaryAsync(serverUrl);

            await InstallScheduledTaskAsync(agentExePath);

            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            config.ServerUrl = serverUrl;
            config.IsAgentEnabled = true;
            config.Registered = false;
            await _configStore.WriteAsync(config);

            new ClientDesktopInstallation().Install(Environment.ProcessPath!,
                typeof(MainForm).Assembly.GetName().Version!.ToString(3), enableTray: true);
            _serverUrlTextBox.Text = serverUrl;

            if (!IsAgentRunning()) await StartManagedAgentAsync(agentExePath);
            _lastStartupCheck = DateTimeOffset.MinValue;
            ShowCompleted(File.Exists(LinkSafeDefaults.ClientStopBackupSignalPath)
                ? "Startup verified. Backups are paused; click Back up now to resume."
                : "Installed in Start and Installed apps. Backup and tray icon start at your Windows login.");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Enable failed: {ex.Message}");
        }
    }

    private async Task RestoreDataAsync(bool promptIfMissing, bool forcePrompt, bool fromAnotherDevice = false)
    {
        if (_restoreRequested || _backupRequested) return;
        _restoreRequested = true;
        _restoreCancellation = new CancellationTokenSource();
        if (promptIfMissing || forcePrompt)
        {
            ShowProgress("Checking for available restore data...");
        }

        try
        {
            var serverUrl = NormalizeServerUrl(_serverUrlTextBox.Text);
            _serverUrlTextBox.Text = serverUrl;

            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            config.ServerUrl = serverUrl;
            await _configStore.WriteAsync(config);

            var status = await _statusStore.LoadAsync(() => new StatusFileModel());
            var identity = ResolveIdentity();

            using var discoveryClient = new HttpClient
            {
                BaseAddress = new Uri(serverUrl),
                Timeout = forcePrompt ? RestoreDiscoveryPromptTimeout : RestoreDiscoveryBackgroundTimeout
            };
            var query =
                $"api/restore/options?domainUser={Uri.EscapeDataString(identity.DomainUser)}&computerName={Uri.EscapeDataString(identity.ComputerName)}&includeCurrentComputer={(forcePrompt && !fromAnotherDevice).ToString().ToLowerInvariant()}";
            var options = await discoveryClient.GetFromJsonAsync<RoamingRestoreOptionsResponse>(query, _restoreCancellation.Token);
            var candidate = options?.Candidates
                .OrderByDescending(item => item.SourceComputerName.Equals(status.AutoRestoreSourceComputer, StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.SourceComputerName.Equals(identity.ComputerName, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.LastBackupTime ?? item.LastSeenAt)
                .FirstOrDefault();

            if (fromAnotherDevice)
            {
                var devices = DeviceMigration.OtherDevices(options?.Candidates ?? [], identity.DomainUser, identity.ComputerName);
                if (devices.Count == 0) { ShowCompleted("No other device has backup data for this Windows account."); return; }
                candidate = ChooseMigrationDevice(devices);
                if (candidate is null) { ShowCompleted("Move data cancelled. No files were changed."); return; }
            }

            if (candidate is null)
            {
                if (promptIfMissing)
                {
                    ShowCompleted("No backup source was found for this user.");
                }

                return;
            }

            if (!forcePrompt &&
                (status.LastRestoreAt is not null ||
                 candidate.SourceComputerName.Equals(status.LastRestorePromptSourceComputer, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var backupTime = candidate.LastBackupTime?.ToLocalTime().ToString("g") ?? "an earlier session";
            ShowProgress("Comparing server files with this device. Existing local files will be preserved...");
            using var restoreClient = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
            await _policyStore.ApplyAsync(config, _restoreCancellation.Token);
            var roots = MissingFileRestore.BuildRoots(config, name => TryResolveRestoreFolder(name, out var path) ? path : "");
            var plan = await MissingFileRestore.CompareAsync(restoreClient, identity.DomainUser, candidate.SourceComputerName, roots,
                _restoreCancellation.Token, config.ProtectedSourcePaths);
            if (plan.Missing.Count == 0)
            {
                if (plan.Warnings.Count > 0) ShowError($"No restorable missing files found. {string.Join("; ", plan.Warnings.Take(3))}");
                else ShowCompleted($"Nothing is missing. Preserved {plan.Existing:N0} existing local files.");
                return;
            }
            var promptMessage =
                $"LinkSafe found backup data for {identity.DomainUser} from {candidate.SourceComputerName} ({backupTime})." +
                $"{Environment.NewLine}{Environment.NewLine}{plan.Missing.Count:N0} files are missing; {plan.Existing:N0} files already exist and will NOT be replaced." +
                $"{Environment.NewLine}{plan.Warnings.Count:N0} comparison warnings. Restore only the missing files now?";

            if (fromAnotherDevice || !forcePrompt)
                promptMessage = DeviceMigration.Preview(identity.DomainUser, candidate.SourceComputerName, plan, roots);

            var dialogResult = MessageBox.Show(
                promptMessage,
                fromAnotherDevice || !forcePrompt ? "Move data to this device" : "LinkSafe Restore",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            status.LastRestorePromptSourceComputer = candidate.SourceComputerName;
            status.LastRestorePromptedAt = DateTimeOffset.UtcNow;
            await _statusStore.WriteAsync(status);

            if (dialogResult != DialogResult.Yes)
            {
                ShowCompleted("Restore skipped.");
                return;
            }

            status.AskBeforeRestoreEveryTime = _askBeforeRestoreCheckBox.Checked;
            await DownloadAndApplyRestoreAsync(restoreClient, plan, candidate, _restoreCancellation.Token);
            await RefreshStatusAsync();
        }
        catch (OperationCanceledException) when (_allowClose) { }
        catch (TaskCanceledException) when (!promptIfMissing && !forcePrompt)
        {
            ShowCompleted("Restore check will continue when the server responds.");
        }
        catch (Exception ex)
        {
            ShowError($"Restore failed: {ex.Message}");
        }
        finally { _restoreRequested = false; _restoreCancellation.Dispose(); _restoreCancellation = null; UpdateActionButtons(); }
    }

    private async Task CheckRestoreDataInBackgroundAsync()
    {
        try
        {
            await Task.Delay(400);
            await RestoreDataAsync(promptIfMissing: false, forcePrompt: false);
        }
        catch
        {
            // Keep startup non-blocking even if restore discovery fails.
        }
    }

    private async Task DisableAgentAsync()
    {
        ShowProgress("Disabling agent...");
        try
        {
            await UninstallScheduledTaskAsync();
            new ClientDesktopInstallation().SetTrayStartup(false);
            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            config.IsAgentEnabled = false;
            await _configStore.WriteAsync(config);

            _lastStartupCheck = DateTimeOffset.MinValue;
            var installedPath = Path.Combine(AgentInstallDirectory, "LinkSafeClientAgent.exe");
            foreach (var process in Process.GetProcessesByName("LinkSafeClientAgent"))
            {
                using (process)
                {
                    if (process.SessionId == Process.GetCurrentProcess().SessionId &&
                        string.Equals(process.MainModule?.FileName, installedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        await process.WaitForExitAsync();
                    }
                }
            }
            ShowCompleted("Agent disabled.");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Disable failed: {ex.Message}");
        }
    }

    private async Task RunBackupNowAsync()
    {
        if (_backupRequested) return;
        _backupRequested = true;
        ShowProgress("Requesting a new backup scan...");
        try
        {
            var serverUrl = NormalizeServerUrl(_serverUrlTextBox.Text);
            var agentExePath = await EnsureAgentBinaryAsync(serverUrl);
            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            config.ServerUrl = serverUrl;
            await _configStore.WriteAsync(config);
            Directory.CreateDirectory(Path.GetDirectoryName(LinkSafeDefaults.ClientRunBackupSignalPath)!);
            File.Delete(LinkSafeDefaults.ClientStopBackupSignalPath);
            var queuedRequest = await ReadQueuedBackupRequestAsync();
            var requestId = Guid.TryParse(queuedRequest, out var queuedId) ? queuedId.ToString("D") : Guid.NewGuid().ToString("D");
            if (string.IsNullOrEmpty(queuedRequest))
            {
                var signalTemp = LinkSafeDefaults.ClientRunBackupSignalPath + "." + requestId + ".tmp";
                await File.WriteAllTextAsync(signalTemp, requestId);
                File.Move(signalTemp, LinkSafeDefaults.ClientRunBackupSignalPath, true);
            }
            else if (!Guid.TryParse(queuedRequest, out _))
                throw new InvalidOperationException("An older backup request is already queued. Let the agent finish it before requesting another scan.");

            if (!IsAgentRunning())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = agentExePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
            }

            var requestedAt = DateTimeOffset.UtcNow;
            DateTimeOffset? untrackedSince = null;
            while (!IsDisposed && !_allowClose)
            {
                var status = await _statusStore.LoadAsync(() => new StatusFileModel());
                if (BackupRequestProgress.IsComplete(status, requestId))
                {
                    if (status.CompletedBackupRequestStatus == "Failed") ShowError(status.CompletedBackupRequestMessage);
                    else if (status.CompletedBackupRequestStatus is "Queued" or "Stopped") ShowPending(status.CompletedBackupRequestMessage);
                    else ShowCompleted(status.CompletedBackupRequestMessage);
                    break;
                }
                if (_stopRequested) ShowPending("Stopping backup safely. Waiting for the agent to finish cancelling...");
                else if (status.LastOperationRequestId == requestId)
                {
                    ShowProgress(string.IsNullOrWhiteSpace(status.LastOperationMessage) ? "Backup in progress..." : status.LastOperationMessage);
                    if (status.BackupProgressPercent is { } percent)
                    {
                        _progressBar.Style = ProgressBarStyle.Blocks;
                        _progressBar.Value = Math.Clamp(percent, 0, 100);
                    }
                }
                else ShowPending(BackupRequestProgress.WaitingMessage(status));
                if (!IsAgentRunning() && DateTimeOffset.UtcNow - requestedAt > TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException("The background agent stopped before confirming this backup. Check the agent logs and enable it again.");
                if (status.LastOperationRequestId != requestId &&
                    !BackupRequestProgress.IsWaitingForAgent(status, requestId, await ReadQueuedBackupRequestAsync()))
                {
                    // Consuming the signal and publishing acknowledgement are separate file writes.
                    untrackedSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - untrackedSince > TimeSpan.FromMinutes(3))
                        throw new InvalidOperationException("The backup request is no longer queued, but the agent has not reported its result. Check the agent logs before trying again.");
                }
                else untrackedSince = null;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Backup failed: {ex.Message}");
        }
        finally { _backupRequested = false; UpdateActionButtons(); }
    }

    private static async Task<string> ReadQueuedBackupRequestAsync()
    {
        try { return (await File.ReadAllTextAsync(LinkSafeDefaults.ClientRunBackupSignalPath)).Trim(); }
        catch (FileNotFoundException) { return string.Empty; }
        catch (DirectoryNotFoundException) { return string.Empty; }
    }

    private async Task StopBackupAsync()
    {
        if (_stopRequested) return;
        _stopRequested = true;
        try
        {
            var installed = Path.Combine(AgentInstallDirectory, "LinkSafeClientAgent.exe");
            if (!File.Exists(installed) || !Version.TryParse(FileVersionInfo.GetVersionInfo(installed).FileVersion, out var version) ||
                version < new Version(1, 0, 6, 0))
                throw new InvalidOperationException("Stop backup requires agent 1.0.6. Update the server downloads, then Disable agent and Enable agent once to install it.");
            var stopId = Guid.NewGuid().ToString("D");
            var path = LinkSafeDefaults.ClientStopBackupSignalPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + stopId + ".tmp";
            await File.WriteAllTextAsync(temporary, stopId);
            File.Move(temporary, path, true);
            ShowPending("Stop requested. Waiting for the agent to cancel; queued packages will be kept.");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (!IsDisposed && !_allowClose && DateTimeOffset.UtcNow < deadline)
            {
                var status = await _statusStore.LoadAsync(() => new StatusFileModel());
                if (status.LastStopRequestId == stopId)
                {
                    ShowPending(status.LastOperationMessage);
                    return;
                }
                if (!IsAgentRunning()) break;
                await Task.Delay(500);
            }
            ShowPending("Stop remains requested. Backups will stay paused when the agent acknowledges it. Check logs if it does not respond.");
        }
        catch (Exception ex) { ShowError("Could not stop backup: " + ex.Message); }
        finally { _stopRequested = false; UpdateActionButtons(); }
    }

    private void OpenLogs()
    {
        var logDir = Path.GetDirectoryName(LinkSafeDefaults.ClientLogFilePath);
        if (!string.IsNullOrEmpty(logDir))
        {
            System.Diagnostics.Process.Start("explorer.exe", logDir);
        }
    }

    private async Task LoadConfigAsync()
    {
        var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
        var embeddedServerUrl = TryGetEmbeddedServerUrlFromFileName();
        var configuredServerUrl = config.ServerUrl?.Trim();

        _hasEmbeddedServerUrl = !string.IsNullOrWhiteSpace(embeddedServerUrl);
        _serverUrlTextBox.Text =
            !string.IsNullOrWhiteSpace(embeddedServerUrl) ? embeddedServerUrl :
            !string.IsNullOrWhiteSpace(configuredServerUrl) ? configuredServerUrl :
            DefaultServerUrl;
        _serverUrlTextBox.ReadOnly = _hasEmbeddedServerUrl;
        _serverUrlTextBox.Text = NormalizeServerUrl(_serverUrlTextBox.Text);

        if (!_hasEmbeddedServerUrl)
        {
            _serverUrlTextBox.Focus();
        }
        var status = await _statusStore.LoadAsync(() => new StatusFileModel());
        _askBeforeRestoreCheckBox.Checked = status.AskBeforeRestoreEveryTime;
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
        await _policyStore.ApplyAsync(config);
        await RefreshPolicyLabelAsync(config);
        await RefreshStartupAsync(config);
        var status = await _statusStore.LoadAsync(() => new StatusFileModel());
        status.AgentEnabled = config.IsAgentEnabled;
        status.Registered = config.Registered || status.Registered;

        var serverUrl = _serverUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(serverUrl) ||
            !Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
        {
            UpdateStatusLabel(status, config);
            return;
        }

        try
        {
            using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync("api/health");
            status.ServerReachable = response.IsSuccessStatusCode;
        }
        catch
        {
            status.ServerReachable = false;
        }

        // Rendering must not write an old status snapshot over the worker's progress.
        var reachable = status.ServerReachable;
        status = await _statusStore.LoadAsync(() => new StatusFileModel());
        status.ServerReachable = reachable;
        status.AgentEnabled = config.IsAgentEnabled;
        status.Registered = config.Registered || status.Registered;
        UpdateStatusLabel(status, config);
    }

    private void UpdateStatusLabel(StatusFileModel status, ClientAgentConfig config)
    {
        UpdateReportedActions(status);
        var connected = status.ServerReachable ? "Connected" : "Not Connected";
        var enabled = status.AgentEnabled ? "Enabled" : "Disabled";
        var selectedFolders = config.BackupFolders.Count == 0 ? "None" : string.Join(", ", config.BackupFolders);
        var additionalFolders = config.CustomFolders.Count == 0 ? "None" : string.Join(", ", config.CustomFolders);
        var restoreStatus = status.LastRestoreAt is null
            ? "Never"
            : $"{status.LastRestoreAt.Value.ToLocalTime():g} from {status.LastRestoreSourceComputer}";
        var autoRestoreStatus = status.AutoRestoreEnabled
            ? $"Enabled from {status.AutoRestoreSourceComputer}"
            : "Disabled";
        var restorePromptMode = status.AskBeforeRestoreEveryTime ? "Ask every time" : "Restore automatically";

        _connectionValue.Text = connected;
        _connectionValue.ForeColor = status.ServerReachable ? Accent : Warning;
        _agentValue.Text = enabled;
        _agentValue.ForeColor = status.AgentEnabled ? Accent : Muted;
        _lastBackupValue.Text = status.LastSuccessfulBackup?.ToLocalTime().ToString("g") ?? "No backups yet";
        _foldersValue.Text = FriendlyFolders(selectedFolders);
        _drivesValue.Text = config.CustomFolders.Count > 2 ? $"{config.CustomFolders.Count} additional folders (see Choose folders)" : additionalFolders;
        _drivesValue.Visible = config.CustomFolders.Count > 0;
        _restoreValue.Text = $"Last restore: {restoreStatus}\nRestore sync: {autoRestoreStatus}\nPrompt: {restorePromptMode}";
        _registrationValue.Text = status.Registered ? "Registered with your server" : "Registration completes in the background when the agent connects.";
        _errorValue.Text = status.LastError ?? string.Empty;
        _errorValue.Visible = !string.IsNullOrWhiteSpace(status.LastError);
        _statusLabel.Text = string.IsNullOrWhiteSpace(FormatStatus(status)) ? "No activity reported yet." : FormatStatus(status);
        if (!_backupRequested && !_restoreRequested && !_stopRequested && !_upgrading && _pendingAction is null &&
            (_reportedBackupActive || DateTime.UtcNow - _lastUiFeedbackAt > TimeSpan.FromSeconds(10)))
        {
            _progressLabel.Text = string.IsNullOrWhiteSpace(status.LastOperationMessage) ? "Ready to back up." : status.LastOperationMessage;
            _progressLabel.ForeColor = status.LastOperationStatus == "Failed" ? Danger : Ink;
            _progressBar.Visible = _reportedBackupActive;
            _progressBar.Style = status.BackupProgressPercent.HasValue ? ProgressBarStyle.Blocks : ProgressBarStyle.Marquee;
            if (status.BackupProgressPercent is { } percent) _progressBar.Value = Math.Clamp(percent, 0, 100);
        }
        if (!string.IsNullOrWhiteSpace(status.LastError) && _progressLabel.Text.Contains(status.LastError, StringComparison.OrdinalIgnoreCase))
            _errorValue.Visible = false;
    }

    private static string FormatStatus(StatusFileModel status)
    {
        var lastOperationFinishedAt = status.LastOperationFinishedAt?.ToLocalTime().ToString("g");
        var message = string.IsNullOrWhiteSpace(status.LastOperationMessage) ? status.LastOperationStatus : status.LastOperationMessage;
        return string.IsNullOrWhiteSpace(lastOperationFinishedAt) ? message : $"{message} ({lastOperationFinishedAt})";
    }

    private void ShowProgress(string message)
    {
        _lastUiFeedbackAt = DateTime.UtcNow;
        UpdateActionButtons();
        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressBar.Visible = true;
        _progressLabel.Visible = true;
        _progressLabel.ForeColor = Accent;
        _progressLabel.Text = message;
    }

    private void ShowCompleted(string message)
    {
        _lastUiFeedbackAt = DateTime.UtcNow;
        UpdateActionButtons();
        _progressBar.Visible = false;
        _progressLabel.Visible = true;
        _progressLabel.ForeColor = Ink;
        _progressLabel.Text = message;
    }

    private void ShowPending(string message)
    {
        _lastUiFeedbackAt = DateTime.UtcNow;
        UpdateActionButtons();
        _progressBar.Visible = false;
        _progressLabel.Visible = true;
        _progressLabel.ForeColor = Color.FromArgb(130, 90, 20);
        _progressLabel.Text = message;
    }

    private void ShowError(string message)
    {
        _lastUiFeedbackAt = DateTime.UtcNow;
        UpdateActionButtons();
        _progressBar.Visible = false;
        _progressLabel.Visible = true;
        _progressLabel.ForeColor = Danger;
        _progressLabel.Text = message;
    }

    private async Task UpdateProgressFromLocalStatusAsync()
    {
        var status = await _statusStore.LoadAsync(() => new StatusFileModel());
        var operation = status.LastOperationStatus?.Trim() ?? string.Empty;
        var message = status.LastOperationMessage?.Trim() ?? string.Empty;
        var progressText = GetProgressText(operation, message);
        if (string.IsNullOrWhiteSpace(progressText))
        {
            return;
        }

        if (IsFinalOperationStatus(operation))
        {
            if (string.Equals(operation, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                ShowError(progressText);
                return;
            }

            _progressBar.Visible = false;
            _progressLabel.Visible = true;
            _progressLabel.ForeColor = Ink;
            _progressLabel.Text = progressText;
            return;
        }

        ShowProgress(progressText);
    }

    private static string GetProgressText(string operation, string message)
    {
        if (string.Equals(operation, "Checking", StringComparison.OrdinalIgnoreCase))
        {
            return "Checking...";
        }

        if (string.Equals(operation, "Packaging", StringComparison.OrdinalIgnoreCase))
        {
            return "Packaging...";
        }

        if (string.Equals(operation, "Uploading", StringComparison.OrdinalIgnoreCase))
        {
            return "Uploading...";
        }

        if (string.Equals(operation, "Success", StringComparison.OrdinalIgnoreCase))
        {
            return "Completed.";
        }

        if (string.Equals(operation, "NoChanges", StringComparison.OrdinalIgnoreCase))
        {
            return "No changes.";
        }

        if (string.Equals(operation, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(message) ? "Failed." : message;
        }

        if (string.Equals(operation, "Queued", StringComparison.OrdinalIgnoreCase))
        {
            return "Upload queued for automatic retry.";
        }

        if (string.Equals(operation, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return "Checking...";
        }

        return string.Empty;
    }

    private static bool IsFinalOperationStatus(string operation)
    {
        return string.Equals(operation, "Success", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(operation, "NoChanges", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(operation, "Queued", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(operation, "Failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(operation, "Stopped", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetEmbeddedServerUrlFromFileName()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(processPath);
        var match = GeneratedSetupNameRegex.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        var host = match.Groups["host"].Value.Trim();
        var portText = match.Groups["port"].Value.Trim();
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(portText, out var port))
        {
            return null;
        }

        return $"http://{host}:{port}";
    }

    private static string NormalizeServerUrl(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Server IP is required.");
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            return $"http://{trimmed}:5055";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Server IP or URL is not valid.");
        }

        if (uri.IsDefaultPort)
        {
            var builder = new UriBuilder(uri) { Port = 5055 };
            return builder.Uri.ToString().TrimEnd('/');
        }

        return uri.ToString().TrimEnd('/');
    }

    private static ClientIdentityInfo ResolveIdentity()
    {
        var domainUser = WindowsIdentity.GetCurrent().Name;
        var username = Environment.UserName;
        var domain = Environment.UserDomainName;
        return new ClientIdentityInfo
        {
            DomainUser = domainUser,
            Username = username,
            Domain = domain,
            ComputerName = Environment.MachineName
        };
    }

    private Task<string> EnsureAgentBinaryAsync(string serverUrl) => UpdateManagedAgentAsync(serverUrl);

    private static async Task InstallScheduledTaskAsync(string agentExePath)
    {
        await new AgentStartupTask().InstallAsync(agentExePath, WindowsIdentity.GetCurrent().User!.Value);
    }

    private static async Task UninstallScheduledTaskAsync()
    {
        await new AgentStartupTask().RemoveAsync(WindowsIdentity.GetCurrent().User!.Value);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ConfigureNotifyIcon()
    {
        _notifyIcon.Icon = Icon;
        _notifyIcon.Text = "LinkSafe Client";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Uninstall LinkSafe Client", null, async (_, _) => await UninstallClientAsync());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _allowClose = true;
            _notifyIcon.Visible = false;
            Close();
        });
        _notifyIcon.ContextMenuStrip = menu;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason != CloseReason.UserClosing)
        {
            _restoreCancellation?.Cancel();
            _notifyIcon.Visible = false;
            _refreshTimer.Stop();
            return;
        }

        e.Cancel = true;
        Hide();
        _notifyIcon.ShowBalloonTip(2000, "LinkSafe Client", "LinkSafe is still running in the system tray.", ToolTipIcon.Info);
    }

    private static bool IsAgentRunning()
    {
        using var current = Process.GetCurrentProcess();
        var running = false;
        foreach (var process in Process.GetProcessesByName("LinkSafeClientAgent"))
        using (process)
        {
            try { running |= !process.HasExited && process.SessionId == current.SessionId &&
                string.Equals(process.MainModule?.FileName, Path.Combine(AgentInstallDirectory, "LinkSafeClientAgent.exe"), StringComparison.OrdinalIgnoreCase); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
        return running;
    }

    private async Task SaveRestorePreferenceAsync()
    {
        var status = await _statusStore.LoadAsync(() => new StatusFileModel());
        status.AskBeforeRestoreEveryTime = _askBeforeRestoreCheckBox.Checked;
        await _statusStore.WriteAsync(status);
        await RefreshStatusAsync();
    }

    private async Task DownloadAndApplyRestoreAsync(
        HttpClient client,
        MissingRestorePlan plan,
        RoamingRestoreCandidate candidate,
        CancellationToken cancellationToken)
    {
        var result = await MissingFileRestore.ApplyAsync(client, plan, (message, percent) =>
        {
            ShowProgress(message);
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Value = percent;
            return Task.CompletedTask;
        }, cancellationToken);
        var status = await _statusStore.LoadAsync(() => new StatusFileModel());
        status.AutoRestoreEnabled = true;
        status.AskBeforeRestoreEveryTime = _askBeforeRestoreCheckBox.Checked;
        status.AutoRestoreSourceComputer = candidate.SourceComputerName;
        if (result.Errors.Count == 0) status.LastRestoreAt = DateTimeOffset.UtcNow;
        status.LastRestoreSourceComputer = candidate.SourceComputerName;
        status.LastRestorePromptSourceComputer = string.Empty;
        status.LastRestorePromptedAt = null;
        status.LastError = result.Errors.Count == 0 ? "" : result.Summary + " " + string.Join("; ", result.Errors.Take(3));
        await _statusStore.WriteAsync(status);

        if (result.Errors.Count == 0) ShowCompleted(result.Summary);
        else ShowError(status.LastError);
    }


    private static bool TryResolveRestoreFolder(string folderName, out string folderPath)
    {
        folderPath = folderName switch
        {
            "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify),
            "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify),
            "Downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), "Downloads"),
            "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify),
            "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify),
            "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic, Environment.SpecialFolderOption.DoNotVerify),
            "Favorites" => Environment.GetFolderPath(Environment.SpecialFolder.Favorites, Environment.SpecialFolderOption.DoNotVerify),
            "AppDataRoaming" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "ChromeProfile" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Google", "Chrome", "User Data"),
            "EdgeProfile" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Microsoft", "Edge", "User Data"),
            "FirefoxProfile" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Mozilla", "Firefox"),
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(folderPath);
    }
}
