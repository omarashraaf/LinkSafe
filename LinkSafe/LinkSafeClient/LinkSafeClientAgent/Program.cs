using LinkSafeClientAgent;
using LinkSafeClientAgent.Services;
using Shared.Constants;
using Shared.Logging;
using System.Threading;
using Shared.Storage;

ClientWorkspace.EnsureCreated();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddProvider(new FileLoggerProvider(LinkSafeDefaults.ClientLogFilePath));

builder.Services.AddHttpClient();
builder.Services.AddSingleton<AgentConfigService>();
builder.Services.AddSingleton<AgentStatusService>();
builder.Services.AddSingleton<QueueStore>();
builder.Services.AddSingleton<ClientIdentityService>();
builder.Services.AddSingleton<FolderMappingService>();
builder.Services.AddSingleton<ClientApiService>();
builder.Services.AddSingleton<BackupPackageBuilder>();
builder.Services.AddSingleton<BackupStateService>();
builder.Services.AddSingleton<RoamingRestorePromptService>();
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddSingleton<RemoteBackupInbox>();
builder.Services.AddHostedService<RemoteBackupWorker>();
builder.Services.AddHostedService<DriveInventoryWorker>();
builder.Services.AddHostedService<Worker>();

using var host = builder.Build();
host.Services.GetRequiredService<BackupCoordinator>().SkipStartupRestorePrompt = args.Any(arg =>
    arg.Equals("--skip-restore-prompt", StringComparison.OrdinalIgnoreCase) ||
    arg.Equals("--skip-restore-prompt=true", StringComparison.OrdinalIgnoreCase));

if (args.Contains("--backup-now", StringComparer.OrdinalIgnoreCase))
{
    Directory.CreateDirectory(Path.GetDirectoryName(LinkSafeDefaults.ClientRunBackupSignalPath)!);
    File.Delete(LinkSafeDefaults.ClientStopBackupSignalPath);
    var temporary = LinkSafeDefaults.ClientRunBackupSignalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    await File.WriteAllTextAsync(temporary, Guid.NewGuid().ToString("D"));
    File.Move(temporary, LinkSafeDefaults.ClientRunBackupSignalPath, true);
}

using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
using var singleInstanceMutex = new Mutex(false, @"Local\LinkSafeClientAgent-" + identity.User!.Value);
if (!singleInstanceMutex.WaitOne(TimeSpan.Zero, false))
{
    return;
}

await host.RunAsync();
