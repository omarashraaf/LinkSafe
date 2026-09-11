using System.Text.Json;
using LinkSafeServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Shared.Constants;
using Shared.Logging;
using Shared.Models;

var bootstrapConfig = LoadBootstrapConfig();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "LinkSafeServer");

builder.WebHost.UseUrls(bootstrapConfig.ServerUrl);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddProvider(new FileLoggerProvider(LinkSafeDefaults.ServerLogFilePath));

Directory.CreateDirectory(LinkSafeDefaults.ServerDataProtectionKeysDirectory);
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(LinkSafeDefaults.ServerDataProtectionKeysDirectory))
    .SetApplicationName("LinkSafeServer");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.Cookie.Name = "LinkSafeAdmin";
        options.Cookie.HttpOnly = true;
        // Lax is more robust for cross-navigation login flows on LAN browsers.
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });

builder.Services.AddAuthorization();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Setup");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
});

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1_100L * 1024L * 1024L;
});

builder.Services.AddSingleton<ServerConfigService>();
builder.Services.AddSingleton<PathSanitizer>();
builder.Services.AddSingleton<BackupJobService>();
builder.Services.AddSingleton<StorageSecurityService>();
builder.Services.AddSingleton<ClientRegistryService>();
builder.Services.AddSingleton<RemoteBackupService>();
builder.Services.AddSingleton<BackupStorageService>();
builder.Services.AddSingleton<BackupPresenceService>();
builder.Services.AddSingleton<RoamingRestoreService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<LogReaderService>();
builder.Services.AddSingleton<DownloadCatalogService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.Use(async (context, next) =>
{
    var configService = context.RequestServices.GetRequiredService<ServerConfigService>();
    var config = await configService.GetAsync(context.RequestAborted);

    var path = context.Request.Path.Value ?? string.Empty;
    var allowAnonymous =
        path.StartsWith("/Setup", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/downloads", StringComparison.OrdinalIgnoreCase);

    if (!config.IsInitialized && !allowAnonymous)
    {
        context.Response.Redirect("/Setup");
        return;
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

app.Run();

static ServerConfig LoadBootstrapConfig()
{
    Directory.CreateDirectory(Path.GetDirectoryName(LinkSafeDefaults.ServerConfigPath)!);

    if (!File.Exists(LinkSafeDefaults.ServerConfigPath))
    {
        var defaultConfig = new ServerConfig();

        File.WriteAllText(
            LinkSafeDefaults.ServerConfigPath,
            JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true }));
        return defaultConfig;
    }

    var json = File.ReadAllText(LinkSafeDefaults.ServerConfigPath);
    return JsonSerializer.Deserialize<ServerConfig>(json) ?? new ServerConfig();
}
