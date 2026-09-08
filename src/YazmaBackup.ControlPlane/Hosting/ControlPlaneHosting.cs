using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using YazmaBackup.Contracts;
using YazmaBackup.ControlPlane;
using YazmaBackup.ControlPlane.Endpoints;
using YazmaBackup.Domain;


namespace YazmaBackup.ControlPlane.Hosting;

internal sealed record ControlPlaneHostSettings(string StateEngine, bool AllowInsecureUiCookie, string AdminKey, bool LegacyAdminKeyEnabled);

internal static class ControlPlaneHosting
{
    internal static ControlPlaneHostSettings AddControlPlaneServices(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 8L * 1024 * 1024;
        });
        var stateRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_DIR")
            ?? Path.Combine(builder.Environment.ContentRootPath, ".local"));
        Directory.CreateDirectory(stateRoot);
        var dataProtectionRoot = Path.Combine(stateRoot, "dataprotection-keys");
        Directory.CreateDirectory(dataProtectionRoot);
        var allowInsecureUiCookie = string.Equals(Environment.GetEnvironmentVariable("YAZMABACKUP_ALLOW_INSECURE_UI_COOKIE"), "true", StringComparison.OrdinalIgnoreCase);
        var haRole = (Environment.GetEnvironmentVariable("YAZMABACKUP_HA_ROLE") ?? "single").Trim().ToLowerInvariant();
        if (haRole is not ("single" or "active" or "standby" or "active-active")) throw new InvalidOperationException("YAZMABACKUP_HA_ROLE must be single, active, standby, or active-active.");
        var haEnabled = haRole is "active" or "standby" or "active-active";
        var stateEngine = (Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_ENGINE") ?? "file").Trim().ToLowerInvariant();
        if (stateEngine is not ("file" or "postgresql")) throw new InvalidOperationException("YAZMABACKUP_STATE_ENGINE must be file or postgresql.");
        if (haRole == "active-active" && stateEngine != "postgresql")
            throw new InvalidOperationException("R12 active-active production mode requires YAZMABACKUP_STATE_ENGINE=postgresql. File-state active-active is no longer accepted as the production topology.");
        var haNodeId = Environment.GetEnvironmentVariable("YAZMABACKUP_NODE_ID") ?? Environment.MachineName;
        var dataProtectionCertificateThumbprint = (Environment.GetEnvironmentVariable("YAZMABACKUP_DP_CERT_THUMBPRINT") ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        var dataProtection = builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionRoot))
            .SetApplicationName("YazmaBackup.ControlPlane");

        if (!string.IsNullOrWhiteSpace(dataProtectionCertificateThumbprint))
        {
            using var certStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            certStore.Open(OpenFlags.ReadOnly);
            var certificate = certStore.Certificates
                .Find(X509FindType.FindByThumbprint, dataProtectionCertificateThumbprint, validOnly: false)
                .OfType<X509Certificate2>()
                .FirstOrDefault(x => x.HasPrivateKey)
                ?? throw new InvalidOperationException("YAZMABACKUP_DP_CERT_THUMBPRINT certificate with private key was not found in LocalMachine\\My.");
            dataProtection.ProtectKeysWithCertificate(certificate);
        }
        else
        {
            if (haEnabled) throw new InvalidOperationException("HA mode requires YAZMABACKUP_DP_CERT_THUMBPRINT so both nodes can decrypt the shared Data Protection key ring.");
            if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
        }

        builder.Services.AddSingleton(new ControlPlaneHaRuntime(stateRoot, haRole, haNodeId, dataProtectionCertificateThumbprint));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "yb-admin-session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = allowInsecureUiCookie ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(sp => new RepositoryKeyVault(stateRoot, sp.GetRequiredService<IDataProtectionProvider>()));
        builder.Services.AddSingleton(sp => new GlobalNasProfileStore(stateRoot, sp.GetRequiredService<IDataProtectionProvider>()));
        builder.Services.AddSingleton<GlobalNasProfileService>();
        builder.Services.AddSingleton<SettingsTransferService>();
        builder.Services.AddSingleton<ControlPlaneDrInventoryService>();
        builder.Services.AddSingleton<TransactionalStateHealthService>();
        builder.Services.AddSingleton<FleetBackupIntelligenceService>();
        builder.Services.AddSingleton<FleetAutopilotService>();
        builder.Services.AddSingleton<PredictiveProtectionService>();
        builder.Services.AddSingleton<ClosedLoopProtectionService>();
        builder.Services.AddSingleton<RecoveryFabricService>();
        builder.Services.AddSingleton<BusinessContinuityService>();
        builder.Services.AddSingleton<BusinessServiceGraphService>();
        builder.Services.AddSingleton<DisasterRecoveryExecutionService>();
        builder.Services.AddSingleton(sp => new AutonomousOrchestrationStore(stateRoot, sp.GetRequiredService<IDataProtectionProvider>()));
        builder.Services.AddSingleton<AutonomousRemediationOrchestratorService>();
        if (haRole != "standby") builder.Services.AddHostedService<AutonomousRemediationOrchestratorService>(services => services.GetRequiredService<AutonomousRemediationOrchestratorService>());
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.AddPolicy("integration", context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
        builder.Services.AddSingleton<OidcSettings>();
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        builder.Services.AddSingleton<OidcClient>();
        builder.Services.AddSingleton<StateStore>();
        builder.Services.AddSingleton<IControlPlaneStore>(services => services.GetRequiredService<StateStore>());
        builder.Services.AddSingleton<IResilienceStore>(services => services.GetRequiredService<StateStore>());
        builder.Services.AddSingleton<IProductionFabricStore>(services => services.GetRequiredService<StateStore>());
        builder.Services.AddSingleton<IBusinessContinuityStore>(services => services.GetRequiredService<StateStore>());
        builder.Services.AddSingleton<IDisasterRecoverySessionStore>(services => services.GetRequiredService<StateStore>());
        builder.Services.AddSingleton<EvidenceSigningKeyStore>();
        builder.Services.AddSingleton<RecoveryEvidenceService>();
        builder.Services.AddSingleton<TransferTelemetryRegistry>();
        builder.Services.AddSingleton<IMeshCentralFleetStore>(services => services.GetRequiredService<StateStore>());
        builder.Services.AddSingleton<MeshCentralConnectorService>();
        builder.Services.AddSingleton<MeshCentralBootstrapTicketService>();
        builder.Services.AddSingleton<MeshCentralDeploymentHostedService>();
        if (haRole != "standby") builder.Services.AddHostedService<MeshCentralDeploymentHostedService>(services => services.GetRequiredService<MeshCentralDeploymentHostedService>());
        if (haRole != "standby") builder.Services.AddHostedService<MeshCentralSyncHostedService>();
        builder.Services.AddSingleton<PilotReadinessService>();
        if (haRole != "standby") builder.Services.AddHostedService<PolicySchedulerService>();
        if (haRole != "standby") builder.Services.AddHostedService<OperationalSentinelService>();
        if (haRole != "standby") builder.Services.AddHostedService<ResilienceOrchestratorService>();
        if (haRole != "standby") builder.Services.AddHostedService<NotificationDispatcherService>();
        if (haRole != "standby") builder.Services.AddHostedService<ProductionFabricOrchestratorService>();
        var legacyAdminKeyEnabled = string.Equals(Environment.GetEnvironmentVariable("YAZMABACKUP_ENABLE_LEGACY_ADMIN_KEY"), "true", StringComparison.OrdinalIgnoreCase);
        var adminKey = legacyAdminKeyEnabled ? Security.RequireSecret("YAZMABACKUP_ADMIN_KEY") : string.Empty;
        return new(stateEngine, allowInsecureUiCookie, adminKey, legacyAdminKeyEnabled);
    }

    internal static async Task InitializeControlPlaneAsync(this WebApplication app)
    {
        var bootstrapUser = Environment.GetEnvironmentVariable("YAZMABACKUP_BOOTSTRAP_ADMIN_USERNAME") ?? "admin";
        var bootstrapDisplayName = Environment.GetEnvironmentVariable("YAZMABACKUP_BOOTSTRAP_ADMIN_DISPLAY_NAME") ?? "YazmaBackup Yönetici";
        var bootstrapPassword = Environment.GetEnvironmentVariable("YAZMABACKUP_BOOTSTRAP_ADMIN_PASSWORD") ?? string.Empty;
        await app.Services.GetRequiredService<IControlPlaneStore>()
            .EnsureBootstrapAdministratorAsync(bootstrapUser, bootstrapDisplayName, bootstrapPassword, CancellationToken.None)
            .ConfigureAwait(false);
        bootstrapPassword = string.Empty;
        Environment.SetEnvironmentVariable("YAZMABACKUP_BOOTSTRAP_ADMIN_PASSWORD", null);

    }
}
