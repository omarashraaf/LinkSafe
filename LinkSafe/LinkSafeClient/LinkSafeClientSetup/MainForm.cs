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

public sealed class MainForm : Form
{
    private const string DefaultServerUrl = LinkSafeDefaults.DefaultServerUrl;
    private static readonly string AgentInstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LinkSafeAgent",
        "bin");
    private static readonly Regex GeneratedSetupNameRegex =
        new(@"^LinkSafeClientSetup-(?<host>.+)-p(?<port>\d+)\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly JsonFileStore<ClientAgentConfig> _configStore = new(LinkSafeDefaults.ClientConfigPath);
    private readonly JsonFileStore<StatusFileModel> _statusStore = new(LinkSafeDefaults.ClientStatusPath);
    private readonly TextBox _serverUrlTextBox = new() { Dock = DockStyle.Fill };
    private readonly Label _statusLabel = new() { Dock = DockStyle.Fill, AutoSize = false, Height = 160 };
    private readonly ProgressBar _progressBar = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Label _progressLabel = new() { Dock = DockStyle.Fill, AutoSize = true, Visible = false };
    private readonly NotifyIcon _notifyIcon = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private bool _hasEmbeddedServerUrl;
    private bool _allowClose;

    public MainForm()
    {
        Text = "LinkSafe Client";
        Width = 760;
        Height = 460;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(16)
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "Server IP", AutoSize = true }, 0, 0);
        layout.Controls.Add(_serverUrlTextBox, 1, 0);

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttonPanel.Controls.Add(CreateButton("Test Connection", async (_, _) => await TestServerAsync()));
        buttonPanel.Controls.Add(CreateButton("Register Client", async (_, _) => await RegisterClientAsync()));
        buttonPanel.Controls.Add(CreateButton("Enable Agent", async (_, _) => await EnableAgentAsync()));
        buttonPanel.Controls.Add(CreateButton("Disable Agent", async (_, _) => await DisableAgentAsync()));
        buttonPanel.Controls.Add(CreateButton("Run Backup Now", async (_, _) => await RunBackupNowAsync()));
        buttonPanel.Controls.Add(CreateButton("Open Logs", (_, _) => OpenLogs()));

        layout.Controls.Add(buttonPanel, 1, 2);
        layout.Controls.Add(_progressBar, 1, 3);
        layout.Controls.Add(_progressLabel, 1, 4);
        layout.Controls.Add(new Label { Text = "Status", AutoSize = true }, 0, 3);
        layout.Controls.Add(_statusLabel, 1, 5);

        ConfigureNotifyIcon();

        Controls.Add(layout);

_refreshTimer.Tick += async (_, _) => { try { await RefreshStatusAsync(); } catch { } };
        Load += async (_, _) =>
        {
            await LoadConfigAsync();
            _refreshTimer.Start();
        };
        FormClosing += OnFormClosing;
    }

    private Button CreateButton(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += handler;
        return button;
    }

    private async Task TestServerAsync()
    {
        ShowProgress("Testing connection...");
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(_serverUrlTextBox.Text.TrimEnd('/')) };
            var response = await client.GetAsync("api/health");
            response.EnsureSuccessStatusCode();
            ShowProgress("Connection successful!");
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
            var serverUrl = _serverUrlTextBox.Text.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                ShowError("Enable failed: server URL is required.");
                return;
            }

            var agentExePath = await EnsureAgentBinaryAsync(serverUrl);

            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            config.ServerUrl = serverUrl;
            config.IsAgentEnabled = true;
            await _configStore.WriteAsync(config);

            await InstallScheduledTaskAsync(agentExePath, serverUrl);

            var psi = new ProcessStartInfo
            {
                FileName = agentExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            ShowProgress("Agent enabled.");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Enable failed: {ex.Message}");
        }
    }

    private async Task DisableAgentAsync()
    {
        ShowProgress("Disabling agent...");
        try
        {
            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            config.IsAgentEnabled = false;
            await _configStore.WriteAsync(config);

            await UninstallScheduledTaskAsync();
            ShowProgress("Agent disabled.");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Disable failed: {ex.Message}");
        }
    }

    private async Task RunBackupNowAsync()
    {
        ShowProgress("Checking...");
        try
        {
            var serverUrl = _serverUrlTextBox.Text.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                ShowError("Backup failed: server URL is required.");
                return;
            }

            var agentExePath = await EnsureAgentBinaryAsync(serverUrl);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = agentExePath,
                Arguments = "--backup-now",
                UseShellExecute = true,
                Verb = "runas"
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                ShowError("Failed to start backup process.");
                return;
            }

            while (!process.HasExited)
            {
                await UpdateProgressFromLocalStatusAsync();
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            // Give the agent one final moment to flush status updates.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            await UpdateProgressFromLocalStatusAsync();
            await RefreshStatusAsync();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowError("Backup start canceled. Administrator approval is required.");
        }
        catch (Exception ex)
        {
            ShowError($"Backup failed: {ex.Message}");
        }
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

        if (!_hasEmbeddedServerUrl)
        {
            _serverUrlTextBox.Focus();
        }
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var serverUrl = _serverUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(serverUrl) ||
            !Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
        {
            return;
        }
        try
        {
            using var client = new HttpClient { BaseAddress = baseUri };
            var status = await _statusStore.LoadAsync(() => new StatusFileModel());
            var response = await client.GetAsync($"api/register/{status.ClientId}/status");
            if (response.IsSuccessStatusCode)
            {
                var serverStatus = await response.Content.ReadFromJsonAsync<StatusFileModel>();
                if (serverStatus != null)
                {
                    status.LastSeenAt = serverStatus.LastSeenAt;
                    status.LastOperationStatus = serverStatus.LastOperationStatus;
                    status.LastOperationFinishedAt = serverStatus.LastOperationFinishedAt;
                    status.LastOperationMessage = serverStatus.LastOperationMessage;
                    await _statusStore.WriteAsync(status);
                }
            }
            UpdateStatusLabel(status);
        }
        catch
        {
        }
    }

    private void UpdateStatusLabel(StatusFileModel status)
    {
        var connected = status.ServerReachable ? "Connected" : "Not Connected";
        var registered = status.Registered ? "Registered" : "Not Registered";
        var enabled = status.AgentEnabled ? "Enabled" : "Disabled";
        _statusLabel.Text =
            $"Connection: {connected}\n" +
            $"Registration: {registered}\n" +
            $"Agent: {enabled}\n" +
            $"Client ID: {status.ClientId}\n" +
            $"Last seen: {status.LastSeenAt?.ToLocalTime():g}\n" +
            $"Last backup: {status.LastSuccessfulBackup?.ToLocalTime():g}\n" +
            $"Last error: {status.LastError}\n" +
            FormatStatus(status);
    }

    private static string FormatStatus(StatusFileModel status)
    {
        var lastOperationFinishedAt = status.LastOperationFinishedAt?.ToLocalTime().ToString("g");
        var message = string.IsNullOrWhiteSpace(status.LastOperationMessage) ? status.LastOperationStatus : status.LastOperationMessage;
        return string.IsNullOrWhiteSpace(lastOperationFinishedAt) ? message : $"{message} ({lastOperationFinishedAt})";
    }

    private void ShowProgress(string message)
    {
        _progressBar.Visible = true;
        _progressLabel.Visible = true;
        _progressLabel.ForeColor = SystemColors.ControlText;
        _progressLabel.Text = message;
    }

    private void ShowError(string message)
    {
        _progressBar.Visible = false;
        _progressLabel.Visible = true;
        _progressLabel.ForeColor = Color.Red;
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
            _progressLabel.ForeColor = SystemColors.ControlText;
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
               string.Equals(operation, "Failed", StringComparison.OrdinalIgnoreCase);
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

    private async Task<string> EnsureAgentBinaryAsync(string serverUrl)
    {
        var installPath = Path.Combine(AgentInstallDirectory, "LinkSafeClientAgent.exe");
        if (File.Exists(installPath))
        {
            return installPath;
        }

        Directory.CreateDirectory(AgentInstallDirectory);
        ShowProgress("Downloading agent...");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var downloadUrl = $"{serverUrl.TrimEnd('/')}/api/downloads/client-agent";
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = new FileStream(installPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target);

        return installPath;
    }

    private async Task RegisterClientAsync()
    {
        ShowProgress("Registering client...");
        try
        {
            var serverUrl = _serverUrlTextBox.Text.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                ShowError("Registration failed: server URL is required.");
                return;
            }

            var identity = ResolveIdentity();
            using var client = new HttpClient { BaseAddress = new Uri(serverUrl) };

            var request = new RegistrationRequest
            {
                DomainUser = identity.DomainUser,
                Username = identity.Username,
                Domain = identity.Domain,
                ComputerName = identity.ComputerName,
                AgentVersion = "1.0.0"
            };

            var response = await client.PostAsJsonAsync("/api/client/register", request);
            var payload = await response.Content.ReadFromJsonAsync<RegistrationResponse>();
            if (!response.IsSuccessStatusCode || payload?.Success != true)
            {
                ShowError($"Registration failed: {payload?.Message ?? response.ReasonPhrase ?? "Unknown error."}");
                return;
            }

            var config = await _configStore.LoadAsync(() => new ClientAgentConfig());
            config.ServerUrl = serverUrl;
            config.Registered = true;
            config.UserFolderName = payload.UserFolderName;
            config.ComputerFolderName = payload.ComputerFolderName;
            config.BackupDestinationId = payload.BackupDestinationId;
            await _configStore.WriteAsync(config);

            ShowProgress("Client registered successfully.");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Registration failed: {ex.Message}");
        }
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

    private static async Task InstallScheduledTaskAsync(string agentExePath, string serverUrl)
    {
        var command =
            $"/Create /TN \"LinkSafe Client Agent\" /TR \"\\\"{agentExePath}\\\"\" /SC ONLOGON /RL HIGHEST /F";
        var createTask = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = command,
            UseShellExecute = true,
            Verb = "runas"
        };
        Process.Start(createTask)?.WaitForExit();

        var startTask = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = "/Run /TN \"LinkSafe Client Agent\"",
            UseShellExecute = true,
            Verb = "runas"
        };
        Process.Start(startTask)?.WaitForExit();
        await Task.CompletedTask;
    }

    private static async Task UninstallScheduledTaskAsync()
    {
        var deleteTask = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = "/Delete /TN \"LinkSafe Client Agent\" /F",
            UseShellExecute = true,
            Verb = "runas"
        };
        Process.Start(deleteTask)?.WaitForExit();
        await Task.CompletedTask;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ConfigureNotifyIcon()
    {
        _notifyIcon.Icon = SystemIcons.Application;
        _notifyIcon.Text = "LinkSafe Client";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
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
        if (_allowClose)
        {
            _notifyIcon.Visible = false;
            _refreshTimer.Stop();
            return;
        }

        e.Cancel = true;
        Hide();
        _notifyIcon.ShowBalloonTip(2000, "LinkSafe Client", "LinkSafe is still running in the system tray.", ToolTipIcon.Info);
    }
}