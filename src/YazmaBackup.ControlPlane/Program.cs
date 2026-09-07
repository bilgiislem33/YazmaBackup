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
using YazmaBackup.Domain;

var builder = WebApplication.CreateBuilder(args);
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
var app = builder.Build();

var bootstrapUser = Environment.GetEnvironmentVariable("YAZMABACKUP_BOOTSTRAP_ADMIN_USERNAME") ?? "admin";
var bootstrapDisplayName = Environment.GetEnvironmentVariable("YAZMABACKUP_BOOTSTRAP_ADMIN_DISPLAY_NAME") ?? "YazmaBackup Yönetici";
var bootstrapPassword = Environment.GetEnvironmentVariable("YAZMABACKUP_BOOTSTRAP_ADMIN_PASSWORD") ?? string.Empty;
await app.Services.GetRequiredService<IControlPlaneStore>()
    .EnsureBootstrapAdministratorAsync(bootstrapUser, bootstrapDisplayName, bootstrapPassword, CancellationToken.None)
    .ConfigureAwait(false);
bootstrapPassword = string.Empty;
Environment.SetEnvironmentVariable("YAZMABACKUP_BOOTSTRAP_ADMIN_PASSWORD", null);

if (!app.Environment.IsDevelopment()) app.UseHsts();

app.Use(async (context, next) =>
{
    var ha = context.RequestServices.GetRequiredService<ControlPlaneHaRuntime>();
    var mutating = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
    if (mutating && context.Request.Path.StartsWithSegments("/api") && !ha.AcceptsMutations)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = "5";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Control Plane node is not accepting mutations.",
            nodeId = ha.NodeId,
            role = ha.Role,
            draining = ha.Draining
        }).ConfigureAwait(false);
        return;
    }

    var reliabilityStartedAtUtc = DateTimeOffset.UtcNow;
    context.Response.OnStarting(() =>
    {
        var elapsedMs = Math.Max(0, (DateTimeOffset.UtcNow - reliabilityStartedAtUtc).TotalMilliseconds);
        context.Response.Headers["X-YazmaBackup-Correlation-ID"] = context.TraceIdentifier;
        context.Response.Headers["Server-Timing"] = $"app;dur={elapsedMs:F1}";
        return Task.CompletedTask;
    });
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = FrontendContentSecurityPolicy.Build(app.Environment.WebRootPath);
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), usb=()";
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    context.Response.Headers["Cache-Control"] = "no-store";
    try
    {
        await next().ConfigureAwait(false);
    }
    catch (DistributedStateConflictException ex)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.Headers["Retry-After"] = "1";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Distributed state conflict. Retry the operation.",
            detail = ex.Message,
            correlationId = context.TraceIdentifier
        }).ConfigureAwait(false);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        const string bearerPrefix = "Bearer ";
        if (!string.IsNullOrWhiteSpace(authorization) && authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var candidate = authorization[bearerPrefix.Length..].Trim();
            if (candidate.StartsWith("ybmt_", StringComparison.Ordinal))
            {
                var store = context.RequestServices.GetRequiredService<IControlPlaneStore>();
                var token = await store.AuthenticateManagementApiTokenAsync(candidate, context.RequestAborted).ConfigureAwait(false);
                if (token is not null) context.User = ManagementAuthorization.CreateApiTokenPrincipal(token);
            }
        }
    }
    await next().ConfigureAwait(false);
});
app.UseAuthorization();
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    var isAdminMutation = context.Request.Path.StartsWithSegments("/api/v1/admin")
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method);
    var usingLegacyAdminKey = legacyAdminKeyEnabled && Security.ConstantTimeEquals(context.Request.Headers["X-YazmaBackup-Admin-Key"].FirstOrDefault(), adminKey);
    var usingCookieIdentity = string.Equals(context.User.Identity?.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal);

    if (isAdminMutation && usingCookieIdentity && !usingLegacyAdminKey && !ValidCsrf(context))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "CSRF validation failed." }).ConfigureAwait(false);
        return;
    }

    var usingApiTokenIdentity = string.Equals(context.User.Identity?.AuthenticationType, ManagementAuthorization.ApiTokenAuthenticationType, StringComparison.Ordinal);
    var recognizedManagementMutation = isAdminMutation && (usingLegacyAdminKey || usingCookieIdentity || usingApiTokenIdentity);
    var auditStore = recognizedManagementMutation ? context.RequestServices.GetRequiredService<IControlPlaneStore>() : null;
    var actor = recognizedManagementMutation ? ManagementAuthorization.Actor(context, adminKey, legacyAdminKeyEnabled) : string.Empty;
    var action = context.GetEndpoint()?.DisplayName ?? context.Request.Path.Value ?? "admin-action";
    if (auditStore is not null)
    {
        var attempt = new AuditEventRecord(
            Guid.NewGuid(), DateTimeOffset.UtcNow, actor, "attempt:" + action, context.Request.Method,
            context.Request.Path.Value ?? "/", StatusCodes.Status100Continue, context.Connection.RemoteIpAddress?.ToString(), context.TraceIdentifier);
        await auditStore.AppendAuditEventAsync(attempt, context.RequestAborted).ConfigureAwait(false);
    }

    await next().ConfigureAwait(false);

    if (auditStore is not null)
    {
        var completion = new AuditEventRecord(
            Guid.NewGuid(), DateTimeOffset.UtcNow, actor, action, context.Request.Method, context.Request.Path.Value ?? "/",
            context.Response.StatusCode, context.Connection.RemoteIpAddress?.ToString(), context.TraceIdentifier);
        try
        {
            await auditStore.AppendAuditEventAsync(completion, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }
});


app.MapGet("/health/live", (ControlPlaneHaRuntime ha) => Results.Ok(new
{
    status = "live",
    nodeId = ha.NodeId,
    role = ha.Role,
    draining = ha.Draining
}));

app.MapGet("/health/ready", (ControlPlaneHaRuntime ha) =>
    ha.ReadyForTraffic
        ? Results.Ok(new { status = "ready", nodeId = ha.NodeId, role = ha.Role, draining = ha.Draining })
        : Results.Json(new { status = "standby", nodeId = ha.NodeId, role = ha.Role, draining = ha.Draining }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/health", () => Results.Ok(new { status = "ok", product = "YazmaBackup", version = "1.2.0", utc = DateTimeOffset.UtcNow }));

app.MapPost("/api/v1/session/login", async (HttpContext http, LoginRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidText(request.Username, 64) || string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
        return Results.Unauthorized();

    ManagementUserRecord? user;
    try { user = await store.AuthenticateManagementUserAsync(request.Username, request.Password, ct).ConfigureAwait(false); }
    catch (ArgumentException) { user = null; }
    if (user is null)
    {
        await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, request.Username.Trim(), "login-failed", "POST", "/api/v1/session/login", 401, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
        return Results.Unauthorized();
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
        new(ClaimTypes.Name, user.Username),
        new("display_name", user.DisplayName),
        new("must_change_password", user.MustChangePassword ? "true" : "false")
    };
    claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = false,
        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
        AllowRefresh = true
    }).ConfigureAwait(false);

    var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    http.Response.Cookies.Append("yb-csrf", csrf, new CookieOptions
    {
        HttpOnly = false,
        SameSite = SameSiteMode.Strict,
        Secure = !allowInsecureUiCookie,
        Path = "/",
        MaxAge = TimeSpan.FromHours(8)
    });
    await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Username, "login-success", "POST", "/api/v1/session/login", 200, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
    return Results.Ok(new LoginResponse(ToSessionUser(user), csrf));
}).RequireRateLimiting("login");

app.MapGet("/api/v1/session/oidc/status", (OidcClient oidc) => Results.Ok(new { enabled = oidc.Enabled }));

app.MapGet("/api/v1/session/oidc/start", async (HttpContext http, OidcClient oidc, IDataProtectionProvider dataProtection, CancellationToken ct) =>
{
    if (!oidc.Enabled) return Results.NotFound();
    var transaction = new OidcLoginTransaction(
        OidcClient.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
        OidcClient.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
        OidcClient.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
        DateTimeOffset.UtcNow);
    var protector = dataProtection.CreateProtector("YazmaBackup.OIDC.Transaction.v1");
    var protectedValue = protector.Protect(JsonSerializer.Serialize(transaction));
    http.Response.Cookies.Append("yb-oidc-tx", protectedValue, new CookieOptions
    {
        HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = !allowInsecureUiCookie, Path = "/", MaxAge = TimeSpan.FromMinutes(10), IsEssential = true
    });
    var authorizationUri = await oidc.BuildAuthorizationUriAsync(transaction, ct).ConfigureAwait(false);
    return Results.Redirect(authorizationUri.ToString());
}).RequireRateLimiting("login");

app.MapGet("/api/v1/session/oidc/callback", async (HttpContext http, OidcClient oidc, IDataProtectionProvider dataProtection, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!oidc.Enabled) return Results.NotFound();
    var code = http.Request.Query["code"].FirstOrDefault();
    var state = http.Request.Query["state"].FirstOrDefault();
    var protectedValue = http.Request.Cookies["yb-oidc-tx"];
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(protectedValue)) return Results.Unauthorized();
    try
    {
        var protector = dataProtection.CreateProtector("YazmaBackup.OIDC.Transaction.v1");
        var transaction = JsonSerializer.Deserialize<OidcLoginTransaction>(protector.Unprotect(protectedValue));
        if (transaction is null || !Security.ConstantTimeEquals(state, transaction.State) || DateTimeOffset.UtcNow - transaction.CreatedAtUtc > TimeSpan.FromMinutes(10)) return Results.Unauthorized();
        var identity = await oidc.ExchangeAndValidateAsync(code, transaction, ct).ConfigureAwait(false);
        var user = await store.FindOrProvisionExternalUserAsync(identity.Issuer, identity.Subject, identity.Username, identity.DisplayName, identity.Roles, ct).ConfigureAwait(false);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()), new(ClaimTypes.Name, user.Username), new("display_name", user.DisplayName),
            new("must_change_password", "false"), new("auth_source", "oidc")
        };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties
        {
            IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8), AllowRefresh = true
        }).ConfigureAwait(false);
        var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        http.Response.Cookies.Append("yb-csrf", csrf, new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Strict, Secure = !allowInsecureUiCookie, Path = "/", MaxAge = TimeSpan.FromHours(8) });
        http.Response.Cookies.Delete("yb-oidc-tx", new CookieOptions { Path = "/", Secure = !allowInsecureUiCookie, SameSite = SameSiteMode.Lax });
        await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Username, "oidc-login", "GET", "/api/v1/session/oidc/callback", 302, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
        return Results.Redirect("/");
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or CryptographicException or JsonException or HttpRequestException)
    {
        await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "oidc", "oidc-login-failed", "GET", "/api/v1/session/oidc/callback", 401, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
        return Results.Unauthorized();
    }
}).RequireRateLimiting("login");

app.MapPost("/api/v1/session/break-glass", async (HttpContext http, BreakGlassLoginRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidText(request.Username, 64) || !ValidText(request.RecoveryCode, 64)) return Results.Unauthorized();
    var user = await store.RedeemBreakGlassRecoveryCodeAsync(request.Username, request.RecoveryCode, ct).ConfigureAwait(false);
    if (user is null)
    {
        await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, request.Username.Trim(), "break-glass-failed", "POST", "/api/v1/session/break-glass", 401, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
        return Results.Unauthorized();
    }
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.UserId.ToString()), new(ClaimTypes.Name, user.Username),
        new("display_name", user.DisplayName), new("must_change_password", "true"), new("break_glass", "true")
    };
    claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties
    {
        IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30), AllowRefresh = false
    }).ConfigureAwait(false);
    var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    http.Response.Cookies.Append("yb-csrf", csrf, new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Strict, Secure = !allowInsecureUiCookie, Path = "/", MaxAge = TimeSpan.FromMinutes(30) });
    await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Username, "break-glass-login", "POST", "/api/v1/session/break-glass", 200, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
    return Results.Ok(new LoginResponse(ToSessionUser(user), csrf));
}).RequireRateLimiting("login");

app.MapGet("/api/v1/session/me", (HttpContext http) =>
{
    if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var userId = ManagementAuthorization.GetUserId(http.User);
    if (userId is null) return Results.Unauthorized();
    var roles = http.User.FindAll(ClaimTypes.Role).Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    var mustChange = string.Equals(http.User.FindFirst("must_change_password")?.Value, "true", StringComparison.OrdinalIgnoreCase);
    return Results.Ok(new SessionUserDto(userId.Value, http.User.Identity.Name ?? string.Empty, http.User.FindFirst("display_name")?.Value ?? http.User.Identity.Name ?? string.Empty, roles, mustChange));
});

app.MapPost("/api/v1/session/logout", async (HttpContext http) =>
{
    if (http.User.Identity?.IsAuthenticated != true) return Results.NoContent();
    if (!ValidCsrf(http)) return Results.BadRequest(new { error = "CSRF validation failed." });
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    http.Response.Cookies.Delete("yb-csrf", new CookieOptions { Path = "/" });
    return Results.NoContent();
});

app.MapPost("/api/v1/session/change-password", async (HttpContext http, ChangeOwnPasswordRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (http.User.Identity?.IsAuthenticated != true || !ValidCsrf(http)) return Results.Unauthorized();
    var userId = ManagementAuthorization.GetUserId(http.User);
    if (userId is null) return Results.Unauthorized();
    try
    {
        var breakGlass = string.Equals(http.User.FindFirst("break_glass")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        var changed = breakGlass
            ? await store.ResetManagementPasswordAfterBreakGlassAsync(userId.Value, request.NewPassword, ct).ConfigureAwait(false)
            : await store.ChangeManagementPasswordAsync(userId.Value, request.CurrentPassword, request.NewPassword, ct).ConfigureAwait(false);
        if (!changed) return Results.Unauthorized();
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        http.Response.Cookies.Delete("yb-csrf", new CookieOptions { Path = "/" });
        return Results.NoContent();
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/v1/agents/register", async (HttpContext http, RegisterAgentRequest request, IControlPlaneStore store, IMeshCentralFleetStore fleet, MeshCentralBootstrapTicketService tickets, CancellationToken ct) =>
{
    if (!ValidMachine(request.MachineName) || !ValidText(request.OperatingSystem, 256) || !ValidText(request.AgentVersion, 64) || (request.Capabilities?.Count ?? 0) > 64 || !ValidOptionalPublicKey(request.KeyExchangePublicKeyPem))
        return Results.BadRequest(new { error = "Invalid agent registration payload." });

    var enrollmentToken = http.Request.Headers["X-YazmaBackup-Enrollment"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(enrollmentToken)) return Results.Unauthorized();
    try
    {
        var (agent, token) = await store.EnrollAgentAsync(
            enrollmentToken, request.MachineName.Trim(), request.OperatingSystem.Trim(), request.AgentVersion.Trim(), SanitizeCapabilities(request.Capabilities), request.KeyExchangePublicKeyPem?.Trim(), ct).ConfigureAwait(false);

        if (tickets.TryConsumeEnrollment(enrollmentToken, out var correlation) && correlation is not null)
        {
            var deployment = (await fleet.GetMeshCentralDeploymentsAsync(5000, ct).ConfigureAwait(false))
                .FirstOrDefault(x => x.DeploymentId == correlation.DeploymentId && x.NodeId == correlation.NodeId);
            if (deployment is not null && deployment.Status is ("dispatching" or "dispatched" or "installing"))
            {
                await fleet.UpdateMeshCentralDeploymentAsync(
                    deployment.DeploymentId, "installing",
                    $"Agent registration bound to exact MeshCentral deployment; waiting authenticated heartbeat. Machine={request.MachineName.Trim()}",
                    deployment.CommandId, agent.AgentId, ct).ConfigureAwait(false);
            }
        }

        return Results.Ok(new RegisterAgentResponse(agent.AgentId, token));
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Unauthorized();
    }
});

app.MapPost("/api/v1/agent/heartbeat", async (HttpContext http, HeartbeatRequest request, IControlPlaneStore store, IMeshCentralFleetStore fleet, GlobalNasProfileService globalNas, CancellationToken ct) =>
{
    var auth = await AuthenticateAgentAsync(http, store, ct).ConfigureAwait(false);
    if (auth is null) return Results.Unauthorized();
    if (!ValidMachine(request.MachineName) || !ValidText(request.OperatingSystem, 256) || !ValidText(request.AgentVersion, 64) || (request.Capabilities?.Count ?? 0) > 64 || !ValidOptionalPublicKey(request.KeyExchangePublicKeyPem))
        return Results.BadRequest(new { error = "Invalid heartbeat payload." });

    var machineName = request.MachineName.Trim();
    var agentVersion = request.AgentVersion.Trim();
    await store.TouchAsync(auth.Value.AgentId, machineName, request.OperatingSystem.Trim(), agentVersion, SanitizeCapabilities(request.Capabilities), request.KeyExchangePublicKeyPem?.Trim(), SanitizeProtection(request.Protection), SanitizeRepositoryCircuits(request.RepositoryCircuits), ct).ConfigureAwait(false);

    // R6.9: global NAS profile is durable on the Control Plane. Applying on heartbeat
    // is idempotent by profile version, so existing Agents are not reprovisioned unless
    // the saved global profile changes; newly enrolled Agents receive it automatically.
    await globalNas.ApplyCurrentToAgentAsync(auth.Value.AgentId, ct).ConfigureAwait(false);

    // R5.11.2: authoritative reconciliation uses the AgentId bound by the one-time
    // deployment enrollment grant. MeshCentral display names and hostnames are only fallback.
    var activeDeployments = await fleet.GetMeshCentralDeploymentsAsync(5000, ct).ConfigureAwait(false);
    var exactDeployments = activeDeployments
        .Where(x => x.AgentId == auth.Value.AgentId && x.Status is ("dispatching" or "dispatched" or "installing"))
        .ToArray();

    foreach (var deployment in exactDeployments)
    {
        await fleet.UpdateMeshCentralDeploymentAsync(
            deployment.DeploymentId,
            "succeeded",
            $"Authenticated heartbeat completed exact deployment binding. Machine={machineName}; Version={agentVersion}",
            deployment.CommandId,
            auth.Value.AgentId,
            ct).ConfigureAwait(false);
        await fleet.MarkMeshCentralDeviceMatchedAsync(
            deployment.NodeId,
            auth.Value.AgentId,
            agentVersion,
            $"deployment:{deployment.DeploymentId:N} → agent:{auth.Value.AgentId}",
            ct).ConfigureAwait(false);
    }

    // R5.8 fallback remains for legacy/non-correlated deployments only.
    var inventory = await fleet.GetMeshCentralInventoryAsync(ct).ConfigureAwait(false);
    static string NormalizeHost(string? value) => (value ?? string.Empty).Trim().Split('.')[0];
    static bool SameHost(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(NormalizeHost(left), NormalizeHost(right), StringComparison.OrdinalIgnoreCase);

    var hostCandidates = inventory
        .Where(x => SameHost(x.Hostname, machineName) || SameHost(x.Name, machineName))
        .Select(x => x.NodeId)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    if (exactDeployments.Length == 0 && hostCandidates.Length == 1)
    {
        var deployments = activeDeployments;
        var reconciledAny = false;
        foreach (var deployment in deployments.Where(x => x.NodeId == hostCandidates[0] && x.Status is ("dispatching" or "dispatched" or "installing")))
        {
            await fleet.UpdateMeshCentralDeploymentAsync(
                deployment.DeploymentId,
                "succeeded",
                $"Agent heartbeat reconciled deployment immediately. Machine={machineName}; Version={agentVersion}",
                deployment.CommandId,
                auth.Value.AgentId,
                ct).ConfigureAwait(false);
            reconciledAny = true;
        }

        // R5.10: keep the Fleet UI's device match status in sync with the deployment outcome.
        // Without this, MeshCentralInventoryDeviceRecord.MatchStatus stays "missing" (device shows
        // as "Agent Yok" on the Mesh Fleet page) until the next full ListDevices sync, even though the
        // deployment record itself already flipped to "succeeded" above.
        if (reconciledAny)
        {
            await fleet.MarkMeshCentralDeviceMatchedAsync(
                hostCandidates[0],
                auth.Value.AgentId,
                agentVersion,
                $"hostname:{machineName} → {machineName} (heartbeat)",
                ct).ConfigureAwait(false);
        }
    }

    return Results.Ok(new HeartbeatResponse(DateTimeOffset.UtcNow));
});

app.MapPost("/api/v1/agent/transfer-telemetry", async (HttpContext http, AgentTransferTelemetryRequest request, IControlPlaneStore store, TransferTelemetryRegistry registry, CancellationToken ct) =>
{
    var auth = await AuthenticateAgentAsync(http, store, ct).ConfigureAwait(false);
    if (auth is null) return Results.Unauthorized();
    var now = DateTimeOffset.UtcNow;
    if (request.CommandId == Guid.Empty || !string.Equals(request.Operation, "backup", StringComparison.Ordinal) ||
        request.State is not ("active" or "completed" or "failed") ||
        (request.RepositoryId is not null && !ValidIdentifier(request.RepositoryId, 128)) ||
        request.BytesTransferred < 0 || request.BytesPerSecond < 0 || request.BytesPerSecond > 8L * 1024 * 1024 * 1024 ||
        request.SampledAtUtc < now.AddMinutes(-5) || request.SampledAtUtc > now.AddSeconds(15) ||
        request.StartedAtUtc > request.SampledAtUtc || request.StartedAtUtc < request.SampledAtUtc.AddDays(-2) ||
        (request.Stage is not null && request.Stage is not ("preparing" or "scanning" or "processing" or "committing" or "completed" or "failed")) ||
        request.LogicalBytesProcessed is < 0 || request.LogicalBytesTotal is < 0 || request.FilesProcessed is < 0 || request.FilesTotal is < 0 ||
        (request.LogicalBytesProcessed.HasValue && request.LogicalBytesTotal.HasValue && request.LogicalBytesProcessed > request.LogicalBytesTotal) ||
        (request.FilesProcessed.HasValue && request.FilesTotal.HasValue && request.FilesProcessed > request.FilesTotal))
        return Results.BadRequest(new { error = "Invalid transfer telemetry payload." });

    registry.Record(auth.Value.AgentId, auth.Value.Record.MachineName, request);
    return Results.NoContent();
});

app.MapGet("/api/v1/agent/commands/next", async (HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
{
    var auth = await AuthenticateAgentAsync(http, store, ct).ConfigureAwait(false);
    if (auth is null) return Results.Unauthorized();
    var cmd = await store.ClaimNextAsync(auth.Value.AgentId, ct).ConfigureAwait(false);
    if (cmd is null) return Results.NoContent();
    if (cmd.LeaseId is null || cmd.LeaseExpiresAtUtc is null) return Results.Problem("Command lease invariant failed.");
    return Results.Ok(new AgentCommandDto(cmd.CommandId, cmd.Type.ToString(), cmd.PayloadJson, cmd.CreatedAtUtc, cmd.LeaseId.Value, cmd.LeaseExpiresAtUtc.Value, cmd.AttemptCount));
});

app.MapPost("/api/v1/agent/commands/{commandId:guid}/lease", async (Guid commandId, HttpContext http, RenewCommandLeaseRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    var auth = await AuthenticateAgentAsync(http, store, ct).ConfigureAwait(false);
    if (auth is null) return Results.Unauthorized();
    try
    {
        var expires = await store.RenewLeaseAsync(auth.Value.AgentId, commandId, request.LeaseId, ct).ConfigureAwait(false);
        return Results.Ok(new RenewCommandLeaseResponse(request.LeaseId, expires));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

app.MapPost("/api/v1/agent/commands/{commandId:guid}/result", async (Guid commandId, HttpContext http, CommandResultRequest request, IControlPlaneStore store, RepositoryKeyVault keyVault, CancellationToken ct) =>
{
    var auth = await AuthenticateAgentAsync(http, store, ct).ConfigureAwait(false);
    if (auth is null) return Results.Unauthorized();
    if (request.ResultJson is null || request.ResultJson.Length > 4 * 1024 * 1024 || (request.Error?.Length ?? 0) > 4096)
        return Results.BadRequest(new { error = "Command result is too large." });
    try
    {
        var completedCommand = await store.GetCommandAsync(commandId, ct).ConfigureAwait(false);
        await store.CompleteAsync(auth.Value.AgentId, commandId, request.LeaseId, request.Succeeded, request.ResultJson, request.Error, ct).ConfigureAwait(false);

        // R6.8 legacy/existing-policy self-heal:
        // if a BackupPath reached an Agent before its repository key ring existed,
        // provision the repository key once and requeue that exact backup payload.
        if (!request.Succeeded &&
            completedCommand is not null &&
            completedCommand.Type == AgentCommandType.BackupPath &&
            completedCommand.AgentId == auth.Value.AgentId &&
            !string.IsNullOrWhiteSpace(request.Error) &&
            request.Error.Contains("Repository key ring is not provisioned", StringComparison.OrdinalIgnoreCase) &&
            !(completedCommand.IdempotencyKey?.StartsWith("autokey:", StringComparison.Ordinal) ?? false))
        {
            var payload = JsonSerializer.Deserialize<BackupPayload>(completedCommand.PayloadJson);
            if (payload is not null)
            {
                var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.AgentId == auth.Value.AgentId);
                if (agent is not null && ValidPublicKey(agent.KeyExchangePublicKeyPem))
                {
                    var vaultKey = keyVault.GetOrCreate(payload.RepositoryId.Trim());
                    byte[]? wrapped = null;
                    try
                    {
                        using var rsa = RSA.Create();
                        rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
                        wrapped = rsa.Encrypt(vaultKey.KeyMaterial, RSAEncryptionPadding.OaepSHA256);

                        await store.EnqueueAsync(
                            auth.Value.AgentId,
                            AgentCommandType.ProvisionRepositoryKey,
                            JsonSerializer.Serialize(new WrappedRepositoryKeyPayload(payload.RepositoryId.Trim(), vaultKey.KeyId, Convert.ToBase64String(wrapped), true)),
                            $"autokey:{commandId:N}:provision",
                            ct).ConfigureAwait(false);

                        await store.EnqueueAsync(
                            auth.Value.AgentId,
                            AgentCommandType.BackupPath,
                            completedCommand.PayloadJson,
                            $"autokey:{commandId:N}:retry",
                            ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(vaultKey.KeyMaterial);
                        if (wrapped is not null) CryptographicOperations.ZeroMemory(wrapped);
                    }
                }
            }
        }

        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});


app.MapGet("/api/v1/bootstrap/meshcentral/{token}", async (string token, MeshCentralBootstrapTicketService tickets, IControlPlaneStore control, IMeshCentralFleetStore fleet, CancellationToken ct) =>
{
    if (!tickets.TryConsumeBootstrap(token, out var bootstrap) || bootstrap is null) return Results.NotFound();
    var packagePath = Environment.GetEnvironmentVariable("YAZMABACKUP_AGENT_PACKAGE_ZIP");
    var publicBase = Environment.GetEnvironmentVariable("YAZMABACKUP_PUBLIC_BASE_URI");
    if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath) || !Uri.TryCreate(publicBase, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps)
        return Results.Problem("Agent deployment package is not configured.", statusCode: 503);
    var enrollment = await control.CreateEnrollmentGrantAsync(TimeSpan.FromMinutes(15), 1, ct).ConfigureAwait(false);
    tickets.TrackEnrollment(enrollment.EnrollmentToken, bootstrap.NodeId, bootstrap.DeploymentId, TimeSpan.FromMinutes(15));
    var package = tickets.IssuePackage(bootstrap.DeploymentId, TimeSpan.FromMinutes(10));
    var packageUrl = new Uri(publicUri, "/api/v1/bootstrap/package/" + Uri.EscapeDataString(package.Token)).ToString();
    var server = publicUri.GetLeftPart(UriPartial.Authority);
    var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath, ct).ConfigureAwait(false))).ToLowerInvariant();
    var deploymentTag = bootstrap.DeploymentId.ToString("N");
    var script = "$ErrorActionPreference='Stop';$w=Join-Path $env:ProgramData ('YazmaBackupDeploy\\'+[guid]::NewGuid().ToString('N'));$fallbackLogRoot=Join-Path $env:ProgramData 'YazmaBackupDeployLogs';New-Item -ItemType Directory -Force -Path $w,$fallbackLogRoot|Out-Null;$tempLog=Join-Path $w 'meshcentral-install-" + deploymentTag + ".log';$fallbackLog=Join-Path $fallbackLogRoot 'meshcentral-install-" + deploymentTag + ".log';$canonicalLog=Join-Path $env:ProgramData 'YazmaBackup\\Logs\\meshcentral-install-" + deploymentTag + ".log';$z=Join-Path $w 'agent.zip';$installSucceeded=$false;try{Invoke-WebRequest -UseBasicParsing -Uri '" + packageUrl.Replace("'", "''", StringComparison.Ordinal) + "' -OutFile $z;if((Get-FileHash -Algorithm SHA256 $z).Hash.ToLowerInvariant() -ne '" + sha + "'){throw 'YazmaBackup Agent package SHA-256 mismatch.'};Expand-Archive $z (Join-Path $w 'bundle') -Force;$env:YAZMABACKUP_ENROLLMENT_TOKEN='" + enrollment.EnrollmentToken + "';$installer=Join-Path $w 'bundle\\INSTALL_AGENT.ps1';$agentPath=Join-Path $w 'bundle\\agent';& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -PublishedAgentPath $agentPath -Server '" + server.Replace("'", "''", StringComparison.Ordinal) + "' *>&1 | Tee-Object -FilePath $tempLog;$installExit=$LASTEXITCODE;if($installExit -ne 0){Copy-Item -LiteralPath $tempLog -Destination $fallbackLog -Force -ErrorAction SilentlyContinue;$tail=((Get-Content -LiteralPath $tempLog -Tail 12 -ErrorAction SilentlyContinue)-join ' | ');throw ('INSTALL_AGENT.ps1 failed. ExitCode='+$installExit+'; Detail='+$tail+'; FallbackLog='+$fallbackLog)};$svc=Get-Service -Name 'YazmaBackupAgent' -ErrorAction SilentlyContinue;if($null -eq $svc -or $svc.Status -ne 'Running'){throw ('YazmaBackupAgent service is not Running after installer. TempLog='+$tempLog)};$identity=Join-Path $env:ProgramData 'YazmaBackup\\agent.json';$access=Join-Path $env:ProgramData 'YazmaBackup\\agent-access-token.dpapi';$deadline=[DateTimeOffset]::UtcNow.AddSeconds(90);while([DateTimeOffset]::UtcNow -lt $deadline -and (-not(Test-Path -LiteralPath $identity -PathType Leaf) -or -not(Test-Path -LiteralPath $access -PathType Leaf))){Start-Sleep -Seconds 2;$svc=Get-Service -Name 'YazmaBackupAgent' -ErrorAction SilentlyContinue;if($null -eq $svc -or $svc.Status -ne 'Running'){throw ('YazmaBackupAgent stopped before enrollment evidence was created. TempLog='+$tempLog)}};if(-not(Test-Path -LiteralPath $identity -PathType Leaf) -or -not(Test-Path -LiteralPath $access -PathType Leaf)){throw ('Agent enrollment evidence was not created within 90 seconds. TempLog='+$tempLog)};Add-Content -LiteralPath $tempLog -Value 'PASS: service=Running; enrollment-evidence=present';$installSucceeded=$true;try{$canonicalLogRoot=Split-Path -Parent $canonicalLog;New-Item -ItemType Directory -Force -Path $canonicalLogRoot|Out-Null;Copy-Item -LiteralPath $tempLog -Destination $canonicalLog -Force -ErrorAction Stop}catch{Copy-Item -LiteralPath $tempLog -Destination $fallbackLog -Force -ErrorAction SilentlyContinue}}catch{if(Test-Path -LiteralPath $tempLog -PathType Leaf){Copy-Item -LiteralPath $tempLog -Destination $fallbackLog -Force -ErrorAction SilentlyContinue};throw}finally{Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue;if($installSucceeded -and (Test-Path -LiteralPath $fallbackLog -PathType Leaf)){Remove-Item -LiteralPath $fallbackLog -Force -ErrorAction SilentlyContinue};Remove-Item $w -Recurse -Force -ErrorAction SilentlyContinue}";
    await fleet.UpdateMeshCentralDeploymentAsync(bootstrap.DeploymentId, "installing", "Bootstrap ticket consumed by target device.", null, null, ct).ConfigureAwait(false);
    return Results.Ok(new { script });
}).RequireRateLimiting("integration");

app.MapGet("/api/v1/bootstrap/package/{token}", (string token, MeshCentralBootstrapTicketService tickets) =>
{
    if (!tickets.TryConsumePackage(token, out var package) || package is null) return Results.NotFound();
    var packagePath = Environment.GetEnvironmentVariable("YAZMABACKUP_AGENT_PACKAGE_ZIP");
    if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath)) return Results.NotFound();
    return Results.File(packagePath, "application/zip", "YazmaBackupAgent.zip", enableRangeProcessing: false);
}).RequireRateLimiting("integration");

var admin = app.MapGroup("/api/v1/admin");
admin.AddEndpointFilter(new AdminAuthenticationFilter(adminKey, legacyAdminKeyEnabled));
var readAdmin = admin.MapGroup(string.Empty);
readAdmin.AddEndpointFilter(new PermissionFilter(adminKey, legacyAdminKeyEnabled, ManagementPermissions.Read));
var operateAdmin = admin.MapGroup(string.Empty);
operateAdmin.AddEndpointFilter(new PermissionFilter(adminKey, legacyAdminKeyEnabled, ManagementPermissions.Operate));
var backupAdmin = admin.MapGroup(string.Empty);
backupAdmin.AddEndpointFilter(new PermissionFilter(adminKey, legacyAdminKeyEnabled, ManagementPermissions.BackupAdministration));
var securityAdmin = admin.MapGroup(string.Empty);
securityAdmin.AddEndpointFilter(new PermissionFilter(adminKey, legacyAdminKeyEnabled, ManagementPermissions.SecurityAdministration));

readAdmin.MapGet("/agents", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
    return Results.Ok(agents.Select(a => new AgentSummaryDto(
        a.AgentId, a.MachineName, a.OperatingSystem, a.AgentVersion,
        a.Capabilities ?? [], a.EnrolledAtUtc, a.LastSeenUtc, a.ProtectionStatus, a.ProtectionReason, a.ProtectionTriggeredAtUtc, a.ProtectionIncidentId, a.RepositoryCircuits ?? [], a.AssignedUser)));
});

operateAdmin.MapPut("/agents/{agentId:guid}/assigned-user", async (Guid agentId, SetAgentAssignedUserRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    try
    {
        var updated = await store.SetAgentAssignedUserAsync(agentId, request.AssignedUser, ct).ConfigureAwait(false);
        if (updated is null) return Results.NotFound();
        return Results.Ok(new { updated.AgentId, updated.AssignedUser });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

readAdmin.MapGet("/fleet/lifecycle", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
    var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
    var rows = agents.Select(a =>
    {
        var ownedPolicies = policies.Where(p => p.AgentId == a.AgentId).ToArray();
        return new
        {
            a.AgentId, a.MachineName, a.AgentVersion, a.LastSeenUtc, a.AssignedUser,
            policyCount = ownedPolicies.Length,
            enabledPolicyCount = ownedPolicies.Count(p => p.Enabled),
            identityPersistent = true,
            policiesServerSide = true,
            lifecycleState = DateTimeOffset.UtcNow - a.LastSeenUtc <= TimeSpan.FromMinutes(3) ? "managed" : "offline"
        };
    }).ToArray();
    return Results.Ok(new
    {
        total = rows.Length,
        managed = rows.Count(x => x.lifecycleState == "managed"),
        offline = rows.Count(x => x.lifecycleState == "offline"),
        policyCount = policies.Count,
        rows
    });
});

readAdmin.MapGet("/pilot/readiness", async (PilotReadinessService pilot, CancellationToken ct) =>
    Results.Ok(await pilot.BuildSummaryAsync(ct).ConfigureAwait(false)));

readAdmin.MapGet("/policy-templates", (PilotReadinessService pilot) => Results.Ok(pilot.GetTemplates()));

operateAdmin.MapPost("/agents/{agentId:guid}/pilot-readiness-probe", async (Guid agentId, HttpContext http, PilotReadinessProbePayload request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
        return Results.BadRequest(new { error = "Source path, repository root or repository id is invalid." });
    if (request.MinimumFreeBytes is < 0 or > 1024L * 1024 * 1024 * 1024 * 1024)
        return Results.BadRequest(new { error = "MinimumFreeBytes is outside the supported range." });
    return await EnqueueAsync(agentId, AgentCommandType.PilotReadinessProbe, request, http, store, ct).ConfigureAwait(false);
});


operateAdmin.MapPost("/agents/{agentId:guid}/deep-validation", async (Guid agentId, HttpContext http, DeepValidationProbePayload request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
        return Results.BadRequest(new { error = "Source path, repository root or repository id is invalid." });
    return await EnqueueAsync(agentId, AgentCommandType.DeepValidationProbe, request, http, store, ct).ConfigureAwait(false);
});

operateAdmin.MapPost("/agents/{agentId:guid}/granular-restore", async (Guid agentId, HttpContext http, EnqueueGranularRestoreRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128) || !ValidIdentifier(request.BackupId, 160) || !ValidPathInput(request.DestinationRoot))
        return Results.BadRequest(new { error = "Invalid granular restore request." });
    if (request.IncludePaths is null || request.IncludePaths.Count is < 1 or > 5000 || request.IncludePaths.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 32767))
        return Results.BadRequest(new { error = "IncludePaths must contain 1..5000 valid relative paths." });
    return await EnqueueAsync(agentId, AgentCommandType.GranularRestore,
        new GranularRestorePayload(request.RepositoryRoot, request.RepositoryId, request.BackupId.Trim(), request.DestinationRoot, request.IncludePaths, request.OverwriteExisting), http, store, ct).ConfigureAwait(false);
});

operateAdmin.MapPost("/agents/{agentId:guid}/restore-sandbox", async (Guid agentId, HttpContext http, EnqueueRestoreSandboxRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128) || !ValidIdentifier(request.BackupId, 160) || !ValidPathInput(request.SandboxRoot))
        return Results.BadRequest(new { error = "Invalid restore sandbox request." });
    if (request.MaxFiles is < 1 or > 100000 || request.MaxBytes is < 1) return Results.BadRequest(new { error = "Sandbox limits are invalid." });
    return await EnqueueAsync(agentId, AgentCommandType.RestoreSandbox,
        new RestoreSandboxPayload(request.RepositoryRoot, request.RepositoryId, request.BackupId.Trim(), request.SandboxRoot, request.MaxFiles, request.MaxBytes), http, store, ct).ConfigureAwait(false);
});

operateAdmin.MapPost("/agents/{agentId:guid}/self-healing/diagnose", async (Guid agentId, HttpContext http, SelfHealingDiagnoseRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
        return Results.BadRequest(new { error = "Invalid self-healing diagnosis request." });
    return await EnqueueAsync(agentId, AgentCommandType.SelfHealingDiagnose,
        new SelfHealingDiagnosePayload(request.SourcePath, request.RepositoryRoot, request.RepositoryId), http, store, ct).ConfigureAwait(false);
});

securityAdmin.MapPost("/agents/{agentId:guid}/self-healing/apply", async (Guid agentId, HttpContext http, SelfHealingApplyRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidIdentifier(request.RepositoryId, 128) || request.ActionId is not ("reset-repository-circuit" or "cleanup-stale-restore-temp"))
        return Results.BadRequest(new { error = "Self-healing action is not allowlisted." });
    if (request.ActionId == "cleanup-stale-restore-temp" && !ValidPathInput(request.WorkingRoot ?? string.Empty))
        return Results.BadRequest(new { error = "WorkingRoot is required for temp cleanup." });
    return await EnqueueAsync(agentId, AgentCommandType.SelfHealingApply,
        new SelfHealingApplyPayload(request.RepositoryId, request.ActionId, request.WorkingRoot), http, store, ct).ConfigureAwait(false);
});

backupAdmin.MapPost("/policy-templates/{templateId}/apply", async (string templateId, ApplyPolicyTemplateRequest request, PilotReadinessService pilot, IControlPlaneStore store, CancellationToken ct) =>
{
    var template = pilot.FindTemplate(templateId);
    if (template is null) return Results.NotFound(new { error = "Policy template not found." });
    if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
        return Results.BadRequest(new { error = "Source path, repository root or repository id is invalid." });
    var name = string.IsNullOrWhiteSpace(request.PolicyName) ? $"{template.Name} · {request.SourcePath}" : request.PolicyName.Trim();
    if (!ValidText(name, 128)) return Results.BadRequest(new { error = "Policy name is invalid." });
    var now = DateTimeOffset.UtcNow;
    var policy = new BackupPolicyRecord(
        Guid.NewGuid(), name, request.AgentId, request.SourcePath, request.RepositoryRoot, request.RepositoryId, request.RequireSnapshot,
        template.IntervalMinutes, template.ActiveBytesPerSecond, template.IdleBytesPerSecond, 300, template.Retention, request.Enabled, now, null, now, template.Protection,
        template.RestoreDrillIntervalDays, null, now.AddDays(template.RestoreDrillIntervalDays), template.RepositoryHealthIntervalHours, null, now);
    try
    {
        var created = await store.CreateBackupPolicyAsync(policy, ct).ConfigureAwait(false);
        return Results.Created($"/api/v1/admin/policies/{created.PolicyId}", ToPolicyDto(created));
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "Agent not found." }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

backupAdmin.MapPost("/policy-templates/{templateId}/apply-bulk", async (string templateId, BulkApplyPolicyTemplateRequest request, PilotReadinessService pilot, IControlPlaneStore store, CancellationToken ct) =>
{
    var template = pilot.FindTemplate(templateId);
    if (template is null) return Results.NotFound(new { error = "Policy template not found." });
    if (request.AgentIds is null || request.AgentIds.Count is < 1 or > 500 || request.AgentIds.Distinct().Count() != request.AgentIds.Count)
        return Results.BadRequest(new { error = "AgentIds must contain 1..500 unique identifiers." });
    if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
        return Results.BadRequest(new { error = "Source path, repository root or repository id is invalid." });

    var now = DateTimeOffset.UtcNow;
    var prefix = string.IsNullOrWhiteSpace(request.PolicyNamePrefix) ? template.Name : request.PolicyNamePrefix.Trim();
    if (!ValidText(prefix, 96)) return Results.BadRequest(new { error = "Policy name prefix is invalid." });
    var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
    var map = agents.ToDictionary(x => x.AgentId);
    if (request.AgentIds.Any(id => !map.ContainsKey(id))) return Results.NotFound(new { error = "One or more Agents were not found. No policies were created." });

    var policies = request.AgentIds.Select(agentId =>
    {
        var machine = map[agentId].MachineName;
        var name = $"{prefix} · {machine}";
        if (name.Length > 128) name = name[..128];
        return new BackupPolicyRecord(Guid.NewGuid(), name, agentId, request.SourcePath, request.RepositoryRoot, request.RepositoryId,
            request.RequireSnapshot, template.IntervalMinutes, template.ActiveBytesPerSecond, template.IdleBytesPerSecond, 300,
            template.Retention, request.Enabled, now, null, now, template.Protection,
            template.RestoreDrillIntervalDays, null, now.AddDays(template.RestoreDrillIntervalDays), template.RepositoryHealthIntervalHours, null, now);
    }).ToArray();

    try
    {
        var created = await store.CreateBackupPoliciesAsync(policies, ct).ConfigureAwait(false);
        return Results.Ok(new BulkApplyPolicyTemplateResultDto(template.TemplateId, created.Count, created.Select(ToPolicyDto).ToArray()));
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

securityAdmin.MapPost("/enrollment-tokens", async (CreateEnrollmentTokenRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (request.ValidForMinutes is < 1 or > 1440 || request.MaxUses is < 1 or > 5000)
        return Results.BadRequest(new { error = "validForMinutes must be 1..1440 and maxUses must be 1..5000." });
    var (grant, token) = await store.CreateEnrollmentGrantAsync(TimeSpan.FromMinutes(request.ValidForMinutes), request.MaxUses, ct).ConfigureAwait(false);
    return Results.Ok(new CreateEnrollmentTokenResponse(grant.GrantId, token, grant.ExpiresAtUtc, request.MaxUses));
});

operateAdmin.MapPost("/agents/{agentId:guid}/browse", async (Guid agentId, HttpContext http, EnqueueBrowseRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.Path)) return Results.BadRequest(new { error = "Path is required and must be <= 32767 characters." });
    return await EnqueueAsync(agentId, AgentCommandType.BrowsePath, new BrowsePayload(request.Path), http, store, ct).ConfigureAwait(false);
});

operateAdmin.MapPost("/agents/{agentId:guid}/backup", async (Guid agentId, HttpContext http, EnqueueBackupRequest request, IControlPlaneStore store, RepositoryKeyVault keyVault, CancellationToken ct) =>
{
    var retention = request.Retention ?? new RetentionPolicy();
    var protection = request.Protection ?? new ProtectionPolicy();
    if (!ValidBackupRequest(request.Path, request.RepositoryRoot, request.RepositoryId, request.ActiveBytesPerSecond, request.IdleBytesPerSecond, request.UserIdleThresholdSeconds, retention, protection, out var error))
        return Results.BadRequest(new { error });

    var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.AgentId == agentId);
    if (agent is null) return Results.NotFound();
    if (!ValidPublicKey(agent.KeyExchangePublicKeyPem))
        return Results.Conflict(new { error = "Agent repository encryption key bootstrap requires a valid Agent key-exchange public key. Update or re-enroll the Agent." });

    RepositoryVaultKey vaultKey;
    try { vaultKey = keyVault.GetOrCreate(request.RepositoryId.Trim()); }
    catch (Exception ex) when (ex is IOException or CryptographicException or InvalidDataException or ArgumentException)
    {
        return Results.Problem($"Repository encryption key vault failed: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    byte[] wrapped;
    try
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
        wrapped = rsa.Encrypt(vaultKey.KeyMaterial, RSAEncryptionPadding.OaepSHA256);
    }
    catch (CryptographicException)
    {
        CryptographicOperations.ZeroMemory(vaultKey.KeyMaterial);
        return Results.Conflict(new { error = "Agent public key could not wrap the repository encryption key." });
    }

    try
    {
        var requestIdempotency = http.Request.Headers["X-YazmaBackup-Idempotency-Key"].FirstOrDefault();
        var keyIdempotency = string.IsNullOrWhiteSpace(requestIdempotency) ? null : requestIdempotency + ":repository-key";
        var keyCommand = await store.EnqueueAsync(
            agentId,
            AgentCommandType.ProvisionRepositoryKey,
            JsonSerializer.Serialize(new WrappedRepositoryKeyPayload(request.RepositoryId.Trim(), vaultKey.KeyId, Convert.ToBase64String(wrapped), true)),
            keyIdempotency,
            ct).ConfigureAwait(false);

        var backupCommand = await store.EnqueueAsync(
            agentId,
            AgentCommandType.BackupPath,
            JsonSerializer.Serialize(new BackupPayload(request.Path, request.RepositoryRoot, request.RepositoryId, request.RequireSnapshot, request.ActiveBytesPerSecond, request.IdleBytesPerSecond, request.UserIdleThresholdSeconds, retention, protection)),
            requestIdempotency,
            ct).ConfigureAwait(false);

        return Results.Accepted(
            $"/api/v1/admin/commands/{backupCommand.CommandId}",
            new
            {
                commandId = backupCommand.CommandId,
                repositoryKeyCommandId = keyCommand.CommandId,
                repositoryKeyId = vaultKey.KeyId,
                repositoryKeyProvisioning = "queued-before-backup"
            });
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    finally
    {
        CryptographicOperations.ZeroMemory(vaultKey.KeyMaterial);
        CryptographicOperations.ZeroMemory(wrapped);
    }
});

operateAdmin.MapPost("/agents/{agentId:guid}/restore-points", async (Guid agentId, HttpContext http, EnqueueListRestorePointsRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128) || !ValidPathInput(request.SourceRoot))
        return Results.BadRequest(new { error = "Repository root, repository id and source root are required." });
    return await EnqueueAsync(agentId, AgentCommandType.ListRestorePoints, new ListRestorePointsPayload(request.RepositoryRoot, request.RepositoryId, request.SourceRoot), http, store, ct).ConfigureAwait(false);
});

operateAdmin.MapPost("/agents/{agentId:guid}/restore-entries", async (Guid agentId, HttpContext http, EnqueueListRestoreEntriesRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128) || !ValidIdentifier(request.BackupId, 160) ||
        (request.Prefix is not null && request.Prefix.Length > 32767))
        return Results.BadRequest(new { error = "Invalid restore explorer request." });
    return await EnqueueAsync(agentId, AgentCommandType.ListRestoreEntries,
        new ListRestoreEntriesPayload(request.RepositoryRoot, request.RepositoryId, request.BackupId.Trim(), request.Prefix), http, store, ct).ConfigureAwait(false);
});

operateAdmin.MapPost("/agents/{agentId:guid}/restore", async (Guid agentId, HttpContext http, EnqueueRestoreRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128) || !ValidPathInput(request.DestinationRoot) || !ValidIdentifier(request.BackupId, 160))
        return Results.BadRequest(new { error = "Invalid restore request." });
    return await EnqueueAsync(agentId, AgentCommandType.RestoreBackup,
        new RestorePayload(request.RepositoryRoot, request.RepositoryId, request.BackupId.Trim(), request.DestinationRoot, request.OverwriteExisting), http, store, ct).ConfigureAwait(false);
});

operateAdmin.MapPost("/agents/{agentId:guid}/restore-point-in-time", async (Guid agentId, HttpContext http, EnqueuePointInTimeRestoreRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128) || !ValidPathInput(request.SourceRoot) || !ValidPathInput(request.DestinationRoot))
        return Results.BadRequest(new { error = "Invalid point-in-time restore request." });
    if (request.RestorePointUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        return Results.BadRequest(new { error = "Restore point cannot be in the future." });
    return await EnqueueAsync(agentId, AgentCommandType.RestorePointInTime,
        new RestorePointInTimePayload(request.RepositoryRoot, request.RepositoryId, request.SourceRoot, request.RestorePointUtc, request.DestinationRoot, request.OverwriteExisting), http, store, ct).ConfigureAwait(false);
});

backupAdmin.MapPost("/agents/{agentId:guid}/scrub", async (Guid agentId, HttpContext http, EnqueueScrubRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
        return Results.BadRequest(new { error = "Invalid repository scrub request." });
    return await EnqueueAsync(agentId, AgentCommandType.ScrubRepository,
        new ScrubRepositoryPayload(request.RepositoryRoot, request.RepositoryId, request.MigrateLegacyPlaintext), http, store, ct).ConfigureAwait(false);
});

backupAdmin.MapPost("/agents/{agentId:guid}/restore-drill", async (Guid agentId, HttpContext http, EnqueueRestoreDrillRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128) || !ValidPathInput(request.SourceRoot))
        return Results.BadRequest(new { error = "Restore drill repository or source path is invalid." });
    return await EnqueueAsync(agentId, AgentCommandType.RestoreDrill,
        new RestoreDrillPayload(request.RepositoryRoot, request.RepositoryId, request.SourceRoot), http, store, ct).ConfigureAwait(false);
});

securityAdmin.MapPost("/settings/export", async (SettingsExportRequest request, SettingsTransferService transfer, CancellationToken ct) =>
{
    try { return Results.Ok(await transfer.ExportAsync(request.Passphrase, ct).ConfigureAwait(false)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

securityAdmin.MapPost("/settings/import", async (SettingsImportRequest request, SettingsTransferService transfer, CancellationToken ct) =>
{
    try { return Results.Ok(await transfer.ImportAsync(request.Passphrase, request.BundleBase64, ct).ConfigureAwait(false)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

readAdmin.MapGet("/nas/global-profile", async (GlobalNasProfileStore profiles, CancellationToken ct) =>
{
    var profile = await profiles.GetAsync(ct).ConfigureAwait(false);
    return profile is null ? Results.NoContent() : Results.Ok(GlobalNasProfileStore.ToSummary(profile));
});

securityAdmin.MapPost("/nas/global-profile", async (SaveGlobalNasProfileRequest request, GlobalNasProfileStore profiles, GlobalNasProfileService profileService, CancellationToken ct) =>
{
    if (!ValidIdentifier(request.RepositoryId, 128) || !ValidPathInput(request.RepositoryRoot) || !ValidText(request.Username, 128))
        return Results.BadRequest(new { error = "Repository, NAS yolu veya kullanıcı adı geçersiz." });

    var existing = await profiles.GetAsync(ct).ConfigureAwait(false);
    var password = request.Password;
    if (string.IsNullOrEmpty(password))
    {
        if (existing is null || string.IsNullOrEmpty(existing.Password))
            return Results.BadRequest(new { error = "İlk global NAS kaydında parola zorunludur." });
        password = existing.Password;
    }

    if (password.Length > 512)
        return Results.BadRequest(new { error = "NAS parolası çok uzun." });

    var saved = await profiles.SaveAsync(request.RepositoryId, request.RepositoryRoot, request.Username, password, ct).ConfigureAwait(false);
    var applied = await profileService.ApplyToAllAgentsAsync(saved, ct).ConfigureAwait(false);
    return Results.Ok(new
    {
        profile = GlobalNasProfileStore.ToSummary(saved),
        queuedAgents = applied.Queued,
        skippedAgents = applied.Skipped,
        passwordReused = string.IsNullOrEmpty(request.Password)
    });
});

securityAdmin.MapPost("/agents/{agentId:guid}/nas-credential/save-and-test", async (Guid agentId, HttpContext http, ProvisionAndTestNasCredentialRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !request.RepositoryRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
        !ValidIdentifier(request.RepositoryId, 128) || string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 128 ||
        string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 128 || request.Username.Any(char.IsControl) || request.Password.Any(char.IsControl))
        return Results.BadRequest(new { error = "NAS yolu, repository, kullanıcı adı veya şifre geçersiz." });

    var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.AgentId == agentId);
    if (agent is null) return Results.NotFound();
    if (!ValidPublicKey(agent.KeyExchangePublicKeyPem))
        return Results.Conflict(new { error = "Agent RSA key-exchange anahtarı yok. Agent güncellenmeli veya yeniden kaydedilmelidir." });

    byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new NasCredentialSecret(request.Username.Trim(), request.Password));
    if (clear.Length > 280)
    {
        CryptographicOperations.ZeroMemory(clear);
        return Results.BadRequest(new { error = "NAS kullanıcı adı/şifre kombinasyonu güvenli taşıma sınırını aşıyor." });
    }

    byte[] wrapped;
    try
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
        wrapped = rsa.Encrypt(clear, RSAEncryptionPadding.OaepSHA256);
    }
    catch (CryptographicException)
    {
        return Results.Conflict(new { error = "Agent public key NAS kimlik bilgisini şifreleyemedi." });
    }
    finally { CryptographicOperations.ZeroMemory(clear); }

    try
    {
        return await EnqueueAsync(agentId, AgentCommandType.ProvisionAndTestNasCredential,
            new WrappedProvisionAndTestNasCredentialPayload(request.RepositoryRoot.Trim(), request.RepositoryId.Trim(), Convert.ToBase64String(wrapped)),
            http, store, ct).ConfigureAwait(false);
    }
    finally { CryptographicOperations.ZeroMemory(wrapped); }
});

securityAdmin.MapPost("/agents/{agentId:guid}/nas-credential", async (Guid agentId, HttpContext http, ProvisionNasCredentialRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidIdentifier(request.RepositoryId, 128) || string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 128 ||
        string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 128 || request.Username.Any(char.IsControl) || request.Password.Any(char.IsControl))
        return Results.BadRequest(new { error = "Repository, NAS username or password is invalid." });

    var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.AgentId == agentId);
    if (agent is null) return Results.NotFound();
    if (!ValidPublicKey(agent.KeyExchangePublicKeyPem))
        return Results.Conflict(new { error = "Agent has no valid key-exchange public key. Upgrade or re-enroll the Agent." });

    byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new NasCredentialSecret(request.Username.Trim(), request.Password));
    if (clear.Length > 280)
    {
        CryptographicOperations.ZeroMemory(clear);
        return Results.BadRequest(new { error = "NAS credential is too long for secure transport." });
    }

    byte[] wrapped;
    try
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
        wrapped = rsa.Encrypt(clear, RSAEncryptionPadding.OaepSHA256);
    }
    catch (CryptographicException)
    {
        return Results.Conflict(new { error = "Agent public key could not encrypt the NAS credential." });
    }
    finally { CryptographicOperations.ZeroMemory(clear); }

    try
    {
        return await EnqueueAsync(agentId, AgentCommandType.ProvisionNasCredential,
            new WrappedNasCredentialPayload(request.RepositoryId.Trim(), Convert.ToBase64String(wrapped)), http, store, ct).ConfigureAwait(false);
    }
    finally { CryptographicOperations.ZeroMemory(wrapped); }
});

operateAdmin.MapPost("/agents/{agentId:guid}/nas-access-test", async (Guid agentId, HttpContext http, TestNasAccessRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !request.RepositoryRoot.StartsWith(@"\\", StringComparison.Ordinal) || !ValidIdentifier(request.RepositoryId, 128))
        return Results.BadRequest(new { error = "NAS test requires a valid UNC repository path and repository id." });
    return await EnqueueAsync(agentId, AgentCommandType.TestNasAccess,
        new TestNasAccessPayload(request.RepositoryRoot.Trim(), request.RepositoryId.Trim()), http, store, ct).ConfigureAwait(false);
});

securityAdmin.MapPost("/agents/{agentId:guid}/repository-key", async (Guid agentId, HttpContext http, ProvisionRepositoryKeyRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidIdentifier(request.RepositoryId, 128) || !ValidIdentifier(request.KeyId, 128) || string.IsNullOrWhiteSpace(request.KeyBase64) || request.KeyBase64.Length > 256)
        return Results.BadRequest(new { error = "Invalid repository key provisioning request." });
    var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.AgentId == agentId);
    if (agent is null) return Results.NotFound();
    if (!ValidPublicKey(agent.KeyExchangePublicKeyPem))
        return Results.Conflict(new { error = "Agent has no valid key-exchange public key. Upgrade or re-enroll the Agent." });

    byte[] clearKey;
    try { clearKey = Convert.FromBase64String(request.KeyBase64); }
    catch (FormatException) { return Results.BadRequest(new { error = "Repository key is not valid Base64." }); }
    if (clearKey.Length != 32)
    {
        CryptographicOperations.ZeroMemory(clearKey);
        return Results.BadRequest(new { error = "Repository key must decode to exactly 32 bytes." });
    }

    byte[] wrapped;
    try
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
        wrapped = rsa.Encrypt(clearKey, RSAEncryptionPadding.OaepSHA256);
    }
    catch (CryptographicException)
    {
        return Results.Conflict(new { error = "Agent key-exchange public key could not encrypt the repository key." });
    }
    finally
    {
        CryptographicOperations.ZeroMemory(clearKey);
    }

    try
    {
        return await EnqueueAsync(agentId, AgentCommandType.ProvisionRepositoryKey,
            new WrappedRepositoryKeyPayload(request.RepositoryId, request.KeyId, Convert.ToBase64String(wrapped), request.MakeActive), http, store, ct).ConfigureAwait(false);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(wrapped);
    }
});

securityAdmin.MapPost("/agents/{agentId:guid}/repository-key/remove", async (Guid agentId, HttpContext http, RemoveRepositoryKeyRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128) || !ValidIdentifier(request.KeyId, 128))
        return Results.BadRequest(new { error = "Invalid repository key removal request." });
    return await EnqueueAsync(agentId, AgentCommandType.RemoveRepositoryKey,
        new RemoveRepositoryKeyPayload(request.RepositoryRoot, request.RepositoryId, request.KeyId), http, store, ct).ConfigureAwait(false);
});

securityAdmin.MapPost("/agents/{agentId:guid}/protection/clear", async (Guid agentId, HttpContext http, ClearProtectionLockRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    return await EnqueueAsync(agentId, AgentCommandType.ClearProtectionLock,
        new ClearProtectionLockPayload(request.ExpectedIncidentId), http, store, ct).ConfigureAwait(false);
});

securityAdmin.MapPost("/agents/{agentId:guid}/stage-update", async (Guid agentId, HttpContext http, EnqueueStageUpdateRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidIdentifier(request.Version, 64) || !ValidSha256(request.Sha256) || string.IsNullOrWhiteSpace(request.SignatureBase64) || request.SignatureBase64.Length > 2048)
        return Results.BadRequest(new { error = "Invalid update metadata." });
    if (!Uri.TryCreate(request.PackageUri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
        return Results.BadRequest(new { error = "Update package URI must use https:// or file://." });
    return await EnqueueAsync(agentId, AgentCommandType.StageAgentUpdate,
        new StageAgentUpdatePayload(request.Version.Trim(), request.PackageUri.Trim(), request.Sha256.ToLowerInvariant(), request.SignatureBase64.Trim()), http, store, ct).ConfigureAwait(false);
});

securityAdmin.MapPost("/agents/{agentId:guid}/apply-staged-update", async (Guid agentId, HttpContext http, ApplyStagedAgentUpdatePayload request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidIdentifier(request.Version, 64) || !Version.TryParse(request.Version, out _))
        return Results.BadRequest(new { error = "Invalid Agent update version." });
    return await EnqueueAsync(agentId, AgentCommandType.ApplyStagedAgentUpdate,
        new ApplyStagedAgentUpdatePayload(request.Version.Trim()), http, store, ct).ConfigureAwait(false);
});

readAdmin.MapGet("/commands/{commandId:guid}", async (Guid commandId, IControlPlaneStore store, CancellationToken ct) =>
{
    var cmd = await store.GetCommandAsync(commandId, ct).ConfigureAwait(false);
    if (cmd is null) return Results.NotFound();
    return Results.Ok(new CommandResultDto(cmd.CommandId, cmd.CompletedAtUtc is not null, cmd.Succeeded, cmd.ResultJson, cmd.Error, cmd.AttemptCount, cmd.LeaseExpiresAtUtc));
});

backupAdmin.MapPost("/policies", async (CreateBackupPolicyRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    var retention = request.Retention ?? new RetentionPolicy();
    var protection = request.Protection ?? new ProtectionPolicy();
    if (!ValidText(request.Name, 128) || request.IntervalMinutes is < 5 or > 43200 || request.RestoreDrillIntervalDays is < 1 or > 365 || request.RepositoryHealthIntervalHours is < 1 or > 720)
        return Results.BadRequest(new { error = "Policy name, backup interval or restore drill interval is invalid." });
    if (!ValidBackupRequest(request.SourcePath, request.RepositoryRoot, request.RepositoryId, request.ActiveBytesPerSecond, request.IdleBytesPerSecond, request.UserIdleThresholdSeconds, retention, protection, out var error))
        return Results.BadRequest(new { error });
    var now = DateTimeOffset.UtcNow;
    var policy = new BackupPolicyRecord(
        Guid.NewGuid(), request.Name.Trim(), request.AgentId, request.SourcePath, request.RepositoryRoot, request.RepositoryId,
        request.RequireSnapshot, request.IntervalMinutes, request.ActiveBytesPerSecond, request.IdleBytesPerSecond,
        request.UserIdleThresholdSeconds, retention, request.Enabled, now, null, now, protection,
        request.RestoreDrillIntervalDays, null, now.AddDays(request.RestoreDrillIntervalDays),
        request.RepositoryHealthIntervalHours, null, now);
    try
    {
        var created = await store.CreateBackupPolicyAsync(policy, ct).ConfigureAwait(false);
        return Results.Created($"/api/v1/admin/policies/{created.PolicyId}", ToPolicyDto(created));
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "Agent not found." }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

readAdmin.MapGet("/policies", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
    return Results.Ok(policies.Select(ToPolicyDto));
});

backupAdmin.MapPost("/policies/{policyId:guid}/enabled", async (Guid policyId, SetPolicyEnabledRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    var policy = await store.SetBackupPolicyEnabledAsync(policyId, request.Enabled, ct).ConfigureAwait(false);
    return policy is null ? Results.NotFound() : Results.Ok(ToPolicyDto(policy));
});

backupAdmin.MapDelete("/policies/{policyId:guid}", async (Guid policyId, IControlPlaneStore store, CancellationToken ct) =>
{
    return await store.DeleteBackupPolicyAsync(policyId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
});

readAdmin.MapGet("/transfer-telemetry", (TransferTelemetryRegistry registry) => Results.Ok(registry.Snapshot()));

readAdmin.MapGet("/backup-history", async (int? limit, IControlPlaneStore store, CancellationToken ct) =>
{
    var commands = await store.GetRecentCommandsAsync(Math.Clamp(limit ?? 100, 1, 500), ct).ConfigureAwait(false);
    var rows = commands
        .Where(c => c.Type == AgentCommandType.BackupPath)
        .OrderByDescending(c => c.CreatedAtUtc)
        .Select(c =>
        {
            BackupResultDto? result = null;
            BackupPayload? request = null;
            if (!string.IsNullOrWhiteSpace(c.PayloadJson))
            {
                try { request = JsonSerializer.Deserialize<BackupPayload>(c.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                catch (JsonException) { }
            }
            if (c.Succeeded && !string.IsNullOrWhiteSpace(c.ResultJson))
            {
                try { result = JsonSerializer.Deserialize<BackupResultDto>(c.ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                catch (JsonException) { }
            }
            return new
            {
                c.CommandId, c.AgentId, c.CreatedAtUtc, c.CompletedAtUtc, c.Succeeded, c.Error,
                errorCategory = ClassifyBackupError(c.Error),
                sourcePath = request?.Path,
                repositoryRoot = request?.RepositoryRoot,
                repositoryId = request?.RepositoryId,
                backupId = result?.BackupId, fileCount = result?.FileCount, logicalBytes = result?.LogicalBytes, uploadedBytes = result?.UploadedBytes,
                newChunks = result?.NewChunks, reusedChunks = result?.ReusedChunks, reusedFiles = result?.ReusedFiles,
                snapshotBacked = result?.SnapshotBacked, incrementalMode = result?.IncrementalMode, encryptionKeyId = result?.EncryptionKeyId
            };
        })
        .ToArray();
    return Results.Ok(rows);
});

readAdmin.MapGet("/dashboard", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
    var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
    var commands = await store.GetRecentCommandsAsync(500, ct).ConfigureAwait(false);
    var now = DateTimeOffset.UtcNow;
    var onlineCutoff = now.AddMinutes(-3);
    return Results.Ok(new DashboardSummaryDto(
        agents.Count,
        agents.Count(a => a.LastSeenUtc >= onlineCutoff),
        agents.Count(a => a.LastSeenUtc < onlineCutoff),
        policies.Count(p => p.Enabled),
        policies.Count(p => !p.Enabled),
        commands.Count(c => c.CompletedAtUtc is null),
        commands.Count(c => c.CompletedAtUtc >= now.AddHours(-24) && !c.Succeeded),
        agents.Count(a => string.Equals(a.ProtectionStatus, "locked", StringComparison.OrdinalIgnoreCase)),
        now));
});


readAdmin.MapGet("/fleet-intelligence", async (FleetBackupIntelligenceService intelligence, CancellationToken ct) =>
    Results.Ok(await intelligence.BuildAsync(ct).ConfigureAwait(false)));

readAdmin.MapGet("/fleet-autopilot", async (FleetAutopilotService autopilot, CancellationToken ct) =>
    Results.Ok(await autopilot.BuildAsync(ct).ConfigureAwait(false)));

readAdmin.MapGet("/predictive-protection", async (PredictiveProtectionService predictive, CancellationToken ct) =>
    Results.Ok(await predictive.BuildAsync(ct).ConfigureAwait(false)));

readAdmin.MapGet("/closed-loop-protection", async (ClosedLoopProtectionService closedLoop, CancellationToken ct) =>
    Results.Ok(await closedLoop.BuildAsync(ct).ConfigureAwait(false)));

readAdmin.MapGet("/recovery-fabric", async (RecoveryFabricService recoveryFabric, CancellationToken ct) =>
    Results.Ok(await recoveryFabric.BuildAsync(ct).ConfigureAwait(false)));

readAdmin.MapGet("/business-continuity", async (BusinessContinuityService continuity, CancellationToken ct) =>
    Results.Ok(await continuity.BuildAsync(ct).ConfigureAwait(false)));

readAdmin.MapGet("/business-service-graph", async (BusinessServiceGraphService graph, CancellationToken ct) =>
    Results.Ok(await graph.BuildAsync(ct).ConfigureAwait(false)));

readAdmin.MapGet("/dr-sessions", async (DisasterRecoveryExecutionService dr, CancellationToken ct) =>
    Results.Ok(await dr.GetAsync(ct).ConfigureAwait(false)));

backupAdmin.MapPost("/dr-sessions", async (CreateDrSessionRequest request, DisasterRecoveryExecutionService dr, CancellationToken ct) =>
{
    try { return Results.Ok(await dr.StartAsync(request.Name, ct).ConfigureAwait(false)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

backupAdmin.MapPost("/dr-sessions/{sessionId:guid}/steps/{order:int}/approve", async (Guid sessionId, int order, DisasterRecoveryExecutionService dr, CancellationToken ct) =>
{
    try { return Results.Ok(await dr.ApproveAsync(sessionId, order, ct).ConfigureAwait(false)); }
    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { return Results.Conflict(new { error = ex.Message }); }
});

backupAdmin.MapPost("/dr-sessions/{sessionId:guid}/steps/{order:int}/verify", async (Guid sessionId, int order, VerifyDrStepRequest request, DisasterRecoveryExecutionService dr, CancellationToken ct) =>
{
    try { return Results.Ok(await dr.VerifyAsync(sessionId, order, request.VerificationNote, ct).ConfigureAwait(false)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { return Results.Conflict(new { error = ex.Message }); }
});

backupAdmin.MapPost("/dr-sessions/{sessionId:guid}/cancel", async (Guid sessionId, DisasterRecoveryExecutionService dr, CancellationToken ct) =>
{
    try { return Results.Ok(await dr.CancelAsync(sessionId, ct).ConfigureAwait(false)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
});

backupAdmin.MapPost("/business-service-dependencies", async (UpsertBusinessServiceDependencyRequest request, IBusinessContinuityStore store, CancellationToken ct) =>
{
    try
    {
        var record = await store.UpsertDependencyAsync(request.ServiceId, request.DependsOnServiceId, request.DependencyType, request.Required, ct).ConfigureAwait(false);
        return Results.Ok(record);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

backupAdmin.MapDelete("/business-service-dependencies/{dependencyId:guid}", async (Guid dependencyId, IBusinessContinuityStore store, CancellationToken ct) =>
    await store.DeleteDependencyAsync(dependencyId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound());

backupAdmin.MapPost("/closed-loop-protection/{caseId}/diagnose", async (string caseId, ClosedLoopProtectionService closedLoop, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(caseId) || caseId.Length > 160)
        return Results.BadRequest(new { error = "Case id is invalid." });
    var run = await closedLoop.StartSafeDiagnosisAsync(caseId, ct).ConfigureAwait(false);
    return run is null
        ? Results.Conflict(new { error = "This case is not eligible for safe automatic diagnosis." })
        : Results.Accepted($"/api/v1/admin/autonomous/remediations/{run.RunId}", run);
});

readAdmin.MapGet("/autonomous-reliability", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
    var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
    var commands = await store.GetRecentCommandsAsync(500, ct).ConfigureAwait(false);
    var onlineCutoff = now.AddMinutes(-3);

    var incidents = new List<object>();
    foreach (var agent in agents.Where(a => a.LastSeenUtc < onlineCutoff))
    {
        incidents.Add(new
        {
            id = $"agent-offline:{agent.AgentId:N}",
            severity = "warning",
            category = "agent-offline",
            agentId = agent.AgentId,
            machineName = agent.MachineName,
            title = "Agent çevrimdışı",
            detail = $"Son bağlantı: {agent.LastSeenUtc:O}",
            safeAutoAction = (string?)null,
            requiresApproval = false
        });
    }

    foreach (var policy in policies.Where(p => p.Enabled && p.NextRunAtUtc < now.AddMinutes(-Math.Max(10, p.IntervalMinutes))))
    {
        incidents.Add(new
        {
            id = $"policy-overdue:{policy.PolicyId:N}",
            severity = "warning",
            category = "policy-overdue",
            agentId = policy.AgentId,
            machineName = agents.FirstOrDefault(a => a.AgentId == policy.AgentId)?.MachineName,
            title = "Yedekleme politikası gecikmiş",
            detail = $"{policy.Name} · sıradaki çalışma {policy.NextRunAtUtc:O}",
            sourcePath = policy.SourcePath,
            repositoryRoot = policy.RepositoryRoot,
            repositoryId = policy.RepositoryId,
            safeAutoAction = "diagnose",
            requiresApproval = false
        });
    }

    foreach (var command in commands.Where(c => c.Type == AgentCommandType.BackupPath && c.CompletedAtUtc is not null && !c.Succeeded && c.CreatedAtUtc >= now.AddHours(-24)))
    {
        BackupPayload? request = null;
        if (!string.IsNullOrWhiteSpace(command.PayloadJson))
        {
            try { request = JsonSerializer.Deserialize<BackupPayload>(command.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { }
        }
        var category = ClassifyBackupError(command.Error);
        var safeAction = category is "repository-circuit" or "repository" ? "reset-repository-circuit" : "diagnose";
        incidents.Add(new
        {
            id = $"backup-failed:{command.CommandId:N}",
            severity = category is "encryption-key" or "nas-authentication" ? "critical" : "warning",
            category,
            agentId = command.AgentId,
            machineName = agents.FirstOrDefault(a => a.AgentId == command.AgentId)?.MachineName,
            title = "Backup başarısız",
            detail = command.Error,
            sourcePath = request?.Path,
            repositoryRoot = request?.RepositoryRoot,
            repositoryId = request?.RepositoryId,
            safeAutoAction = safeAction,
            requiresApproval = safeAction == "reset-repository-circuit"
        });
    }

    foreach (var agent in agents.Where(a => string.Equals(a.ProtectionStatus, "locked", StringComparison.OrdinalIgnoreCase)))
    {
        incidents.Add(new
        {
            id = $"protection-locked:{agent.AgentId:N}",
            severity = "critical",
            category = "protection-locked",
            agentId = agent.AgentId,
            machineName = agent.MachineName,
            title = "Protection lock aktif",
            detail = "Endpoint koruması kilitli durumda.",
            safeAutoAction = (string?)null,
            requiresApproval = true
        });
    }

    var critical = incidents.Count(x => string.Equals((string?)x.GetType().GetProperty("severity")?.GetValue(x), "critical", StringComparison.OrdinalIgnoreCase));
    var warning = incidents.Count - critical;
    var recentBackupAttempts = commands.Count(c => c.Type == AgentCommandType.BackupPath && c.CompletedAtUtc is not null && c.CreatedAtUtc >= now.AddHours(-24));
    var recentBackupFailures = commands.Count(c => c.Type == AgentCommandType.BackupPath && c.CompletedAtUtc is not null && c.CreatedAtUtc >= now.AddHours(-24) && !c.Succeeded);
    var backupSuccessPct = recentBackupAttempts == 0 ? 100d : Math.Round(100d * (recentBackupAttempts - recentBackupFailures) / recentBackupAttempts, 1);
    var onlinePct = agents.Count == 0 ? 100d : Math.Round(100d * agents.Count(a => a.LastSeenUtc >= onlineCutoff) / agents.Count, 1);
    var score = (int)Math.Round(Math.Clamp(backupSuccessPct * .55 + onlinePct * .30 + (critical == 0 ? 15 : 0), 0, 100));
    var canaryReady = score >= 95 && critical == 0 && recentBackupFailures == 0;

    return Results.Ok(new
    {
        generatedAtUtc = now,
        score,
        canaryReady,
        rolloutGuard = canaryReady ? "pass" : "hold",
        criticalIncidents = critical,
        warningIncidents = warning,
        backupSuccessPct,
        agentOnlinePct = onlinePct,
        incidents = incidents.Take(200).ToArray(),
        policy = new
        {
            autonomousDiagnosis = true,
            silentMutation = false,
            allowlistedMutations = AutonomousMutationAllowlist.Values,
            approvalRequiredForMutation = true
        }
    });
});


readAdmin.MapGet("/autonomous/remediations", async (AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
    Results.Ok(await orchestration.GetRemediationsAsync(ct).ConfigureAwait(false)));

securityAdmin.MapPost("/autonomous/remediations", async (CreateAutonomousRemediationRequest request, AutonomousOrchestrationStore orchestration, IControlPlaneStore store, CancellationToken ct) =>
{
    if (request.AgentId == Guid.Empty || !ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
        return Results.BadRequest(new { error = "Remediation target/source/repository input is invalid." });
    if (request.ActionId is not ("diagnose-only" or "reset-repository-circuit" or "cleanup-stale-restore-temp"))
        return Results.BadRequest(new { error = "Remediation action is not allowlisted." });
    if (request.ActionId == "cleanup-stale-restore-temp" && !ValidPathInput(request.WorkingRoot ?? string.Empty))
        return Results.BadRequest(new { error = "WorkingRoot is required for temp cleanup." });
    var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
    if (!agents.Any(x => x.AgentId == request.AgentId)) return Results.NotFound(new { error = "Agent not found." });
    var run = await orchestration.CreateRemediationAsync(request, ct).ConfigureAwait(false);
    return Results.Created($"/api/v1/admin/autonomous/remediations/{run.RunId}", run);
});

securityAdmin.MapPost("/autonomous/remediations/{runId:guid}/approve", async (Guid runId, HttpContext http, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
{
    var actor = ManagementAuthorization.Actor(http, adminKey, legacyAdminKeyEnabled);
    var run = await orchestration.UpdateRemediationAsync(runId, x =>
        x.State == "awaiting-approval"
            ? x with { State = "approved", ApprovedAtUtc = DateTimeOffset.UtcNow, ApprovedBy = actor, Error = null }
            : x, ct).ConfigureAwait(false);
    return run is null ? Results.NotFound() : run.State == "approved" ? Results.Ok(run) : Results.Conflict(new { error = "Remediation is not awaiting approval.", run.State });
});

securityAdmin.MapPost("/autonomous/remediations/{runId:guid}/cancel", async (Guid runId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
{
    var run = await orchestration.UpdateRemediationAsync(runId, x =>
        x.State is "completed" or "failed" ? x : x with { State = "cancelled" }, ct).ConfigureAwait(false);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

readAdmin.MapGet("/autonomous/rollouts", async (AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
    Results.Ok(await orchestration.GetRolloutsAsync(ct).ConfigureAwait(false)));

readAdmin.MapGet("/autonomous/rollouts/{rolloutId:guid}", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
{
    var rollout = await orchestration.GetRolloutAsync(rolloutId, ct).ConfigureAwait(false);
    return rollout is null ? Results.NotFound() : Results.Ok(rollout);
});

securityAdmin.MapPost("/autonomous/rollouts", async (CreateCanaryRolloutRequest request, AutonomousOrchestrationStore orchestration, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ValidText(request.Name, 128) || !ValidIdentifier(request.TargetVersion, 64) || !Version.TryParse(request.TargetVersion, out var targetVersion) || targetVersion is null)
        return Results.BadRequest(new { error = "Rollout name or target version is invalid." });
    if (!ValidSha256(request.Sha256) || string.IsNullOrWhiteSpace(request.SignatureBase64) || request.SignatureBase64.Length > 2048)
        return Results.BadRequest(new { error = "Rollout package hash/signature is invalid." });
    if (!Uri.TryCreate(request.PackageUri, UriKind.Absolute, out var packageUri) || (packageUri.Scheme != Uri.UriSchemeHttps && packageUri.Scheme != Uri.UriSchemeFile))
        return Results.BadRequest(new { error = "Rollout package URI must use https:// or file://." });
    if (request.CanaryPercent is < 1 or > 50 || request.MaxParallel is < 1 or > 25 || request.ObservationMinutes is < 1 or > 60 ||
        request.RequiredScore is < 90 or > 100 || request.MinBackupSuccessPct is < 90 or > 100 || request.MinAgentOnlinePct is < 80 or > 100)
        return Results.BadRequest(new { error = "Rollout safety thresholds are outside supported ranges." });

    var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
    var requestedIds = request.TargetAgentIds?.Distinct().ToHashSet() ?? new HashSet<Guid>();
    var candidates = agents
        .Where(a => requestedIds.Count == 0 || requestedIds.Contains(a.AgentId))
        .Where(a => Version.TryParse(a.AgentVersion, out var current) && current is not null && current < targetVersion)
        .ToArray();
    if (requestedIds.Count > 0 && candidates.Length != requestedIds.Count)
        return Results.BadRequest(new { error = "One or more requested Agents are missing, have unknown versions, or are already at/newer than target." });
    if (candidates.Length == 0) return Results.BadRequest(new { error = "No eligible Agents for this rollout." });

    var rollout = await orchestration.CreateRolloutAsync(request, candidates, ct).ConfigureAwait(false);
    return Results.Created($"/api/v1/admin/autonomous/rollouts/{rollout.RolloutId}", rollout);
});

securityAdmin.MapPost("/autonomous/rollouts/{rolloutId:guid}/start", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
{
    var rollout = await orchestration.UpdateRolloutAsync(rolloutId, x =>
        x.State == "draft" ? x with { State = "preflight", HoldReason = null, HeldFromState = null } : x, ct).ConfigureAwait(false);
    return rollout is null ? Results.NotFound() : rollout.State == "preflight" ? Results.Ok(rollout) : Results.Conflict(new { error = "Rollout must be draft before start.", rollout.State });
});

securityAdmin.MapPost("/autonomous/rollouts/{rolloutId:guid}/hold", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
{
    var rollout = await orchestration.UpdateRolloutAsync(rolloutId, x =>
        x.State is "completed" or "cancelled" or "failed" or "held" ? x : x with { HeldFromState = x.State, State = "held", HoldReason = "Operator hold." }, ct).ConfigureAwait(false);
    return rollout is null ? Results.NotFound() : Results.Ok(rollout);
});

securityAdmin.MapPost("/autonomous/rollouts/{rolloutId:guid}/resume", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
{
    var rollout = await orchestration.UpdateRolloutAsync(rolloutId, x =>
        x.State == "held" ? x with { State = x.HeldFromState ?? "preflight", HeldFromState = null, HoldReason = null } : x, ct).ConfigureAwait(false);
    return rollout is null ? Results.NotFound() : Results.Ok(rollout);
});

securityAdmin.MapPost("/autonomous/rollouts/{rolloutId:guid}/cancel", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
{
    var rollout = await orchestration.UpdateRolloutAsync(rolloutId, x =>
        x.State == "completed" ? x : x with { State = "cancelled", HoldReason = "Operator cancelled." }, ct).ConfigureAwait(false);
    return rollout is null ? Results.NotFound() : Results.Ok(rollout);
});


readAdmin.MapGet("/state-engine/status", (TransactionalStateHealthService stateHealth) =>
{
    try { return Results.Ok(stateHealth.GetStatus()); }
    catch (Exception ex) { return Results.Json(new { engine = stateEngine, healthy = false, error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
});

readAdmin.MapGet("/ha/status", (ControlPlaneHaRuntime ha, ControlPlaneDrInventoryService dr) =>
    Results.Ok(new
    {
        nodeId = ha.NodeId,
        role = ha.Role,
        haEnabled = ha.HaEnabled,
        draining = ha.Draining,
        readyForTraffic = ha.ReadyForTraffic,
        acceptsMutations = ha.AcceptsMutations,
        stateRoot = ha.StateRoot,
        dataProtectionMode = ha.CertificateProtected ? "certificate" : "dpapi-local-machine",
        dataProtectionCertificateThumbprint = ha.CertificateProtected ? ha.CertificateThumbprint : null,
        dr = dr.GetInventory()
    }));

securityAdmin.MapPost("/ha/drain", (ControlPlaneHaRuntime ha) =>
{
    ha.SetDraining(true);
    return Results.Ok(new { nodeId = ha.NodeId, role = ha.Role, draining = ha.Draining, readyForTraffic = ha.ReadyForTraffic });
});

securityAdmin.MapPost("/ha/undrain", (ControlPlaneHaRuntime ha) =>
{
    if (ha.Role == "standby") return Results.Conflict(new { error = "Standby node cannot be undrained for production traffic. Promote the node by configuration and restart." });
    ha.SetDraining(false);
    return Results.Ok(new { nodeId = ha.NodeId, role = ha.Role, draining = ha.Draining, readyForTraffic = ha.ReadyForTraffic });
});

readAdmin.MapGet("/dr/inventory", (ControlPlaneDrInventoryService dr) => Results.Ok(dr.GetInventory()));

readAdmin.MapGet("/operational-health", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
    var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
    var now = DateTimeOffset.UtcNow;
    var onlineCutoff = now.AddMinutes(-3);
    var metrics = await store.GetOperationalCommandMetricsAsync(now.AddHours(-24), now.AddDays(-30), ct).ConfigureAwait(false);
    var successfulBackups = metrics.SuccessfulBackups;
    var failedBackups = metrics.FailedBackups;
    var successfulDrills = metrics.SuccessfulRestoreDrills;
    var failedDrills = metrics.FailedRestoreDrills;
    var enabledPolicies = policies.Where(p => p.Enabled).ToArray();
    var overduePolicies = enabledPolicies.Count(p => p.NextRunAtUtc < now.AddMinutes(-Math.Max(10, p.IntervalMinutes)));
    var lockedAgents = agents.Count(a => string.Equals(a.ProtectionStatus, "locked", StringComparison.OrdinalIgnoreCase));
    var offlineAgents = agents.Count(a => a.LastSeenUtc < onlineCutoff);

    var score = 100;
    if (agents.Count > 0) score -= (int)Math.Round(25d * offlineAgents / agents.Count);
    if (enabledPolicies.Length > 0) score -= (int)Math.Round(20d * overduePolicies / enabledPolicies.Length);
    var backupAttempts = successfulBackups + failedBackups;
    if (backupAttempts > 0) score -= (int)Math.Round(25d * failedBackups / backupAttempts);
    else if (enabledPolicies.Length > 0) score -= 10;
    var drillAttempts = successfulDrills + failedDrills;
    if (drillAttempts > 0) score -= (int)Math.Round(20d * failedDrills / drillAttempts);
    else if (enabledPolicies.Length > 0) score -= 10;
    if (lockedAgents > 0) score -= Math.Min(20, 10 + lockedAgents);
    score = Math.Clamp(score, 0, 100);
    var grade = score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : "E";

    return Results.Ok(new OperationalHealthDto(score, grade, successfulBackups, failedBackups, successfulDrills, failedDrills, overduePolicies, lockedAgents, offlineAgents, now));
});

readAdmin.MapGet("/commands", async (int? limit, IControlPlaneStore store, CancellationToken ct) =>
{
    var commands = await store.GetRecentCommandsAsync(Math.Clamp(limit ?? 100, 1, 500), ct).ConfigureAwait(false);
    return Results.Ok(commands.Select(c => new
    {
        c.CommandId, c.AgentId, type = c.Type.ToString(), c.CreatedAtUtc, c.CompletedAtUtc, c.Succeeded, c.Error, c.AttemptCount
    }));
});

securityAdmin.MapGet("/audit", async (int? limit, IControlPlaneStore store, CancellationToken ct) =>
{
    var events = await store.GetAuditEventsAsync(Math.Clamp(limit ?? 100, 1, 1000), ct).ConfigureAwait(false);
    return Results.Ok(events.Select(a => new AuditEventDto(a.EventId, a.OccurredAtUtc, a.Actor, a.Action, a.Method, a.Path, a.StatusCode, a.RemoteAddress, a.CorrelationId)));
});

securityAdmin.MapGet("/users", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var users = await store.GetManagementUsersAsync(ct).ConfigureAwait(false);
    return Results.Ok(users.Select(ToManagementUserDto));
});

securityAdmin.MapPost("/users", async (HttpContext http, CreateManagementUserRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (request.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase) && !ManagementAuthorization.IsInteractiveAdministrator(http.User))
        return Results.Forbid();
    try
    {
        var user = await store.CreateManagementUserAsync(request.Username, request.DisplayName, request.Password, request.Roles, request.MustChangePassword, ct).ConfigureAwait(false);
        return Results.Created($"/api/v1/admin/users/{user.UserId}", ToManagementUserDto(user));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

securityAdmin.MapPost("/users/{userId:guid}/enabled", async (Guid userId, HttpContext http, SetManagementUserEnabledRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    var target = (await store.GetManagementUsersAsync(ct).ConfigureAwait(false)).FirstOrDefault(u => u.UserId == userId);
    if (target is null) return Results.NotFound();
    if (target.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase) && !ManagementAuthorization.IsInteractiveAdministrator(http.User))
        return Results.Forbid();
    try
    {
        var user = await store.SetManagementUserEnabledAsync(userId, request.Enabled, ct).ConfigureAwait(false);
        return user is null ? Results.NotFound() : Results.Ok(ToManagementUserDto(user));
    }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});


readAdmin.MapGet("/alarms", async (int? limit, bool? includeResolved, IControlPlaneStore store, CancellationToken ct) =>
{
    var alarms = await store.GetAlarmsAsync(Math.Clamp(limit ?? 200, 1, 1000), includeResolved ?? false, ct).ConfigureAwait(false);
    return Results.Ok(alarms.Select(ToAlarmDto));
});

readAdmin.MapGet("/alarms/summary", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var alarms = await store.GetAlarmsAsync(2000, true, ct).ConfigureAwait(false);
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(new AlarmSummaryDto(
        alarms.Count(a => a.Status != AlarmStatus.Resolved && a.Severity == AlarmSeverity.Critical),
        alarms.Count(a => a.Status != AlarmStatus.Resolved && a.Severity == AlarmSeverity.Warning),
        alarms.Count(a => a.Status != AlarmStatus.Resolved && a.Severity == AlarmSeverity.Info),
        alarms.Count(a => a.Status == AlarmStatus.Acknowledged),
        alarms.Count(a => a.Status == AlarmStatus.Resolved), now));
});

operateAdmin.MapPost("/alarms/{alarmId:guid}/acknowledge", async (Guid alarmId, HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
{
    var actor = ManagementAuthorization.Actor(http, adminKey, legacyAdminKeyEnabled);
    var alarm = await store.AcknowledgeAlarmAsync(alarmId, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    return alarm is null ? Results.NotFound() : Results.Ok(ToAlarmDto(alarm));
});

operateAdmin.MapPost("/alarms/{alarmId:guid}/workflow", async (Guid alarmId, HttpContext http, UpdateAlarmWorkflowRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    var actor = ManagementAuthorization.Actor(http, adminKey, legacyAdminKeyEnabled);
    try
    {
        var alarm = await store.UpdateAlarmWorkflowAsync(alarmId, request.AssignedTo, request.Note, request.DueAtUtc, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return alarm is null ? Results.NotFound() : Results.Ok(ToAlarmDto(alarm));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

operateAdmin.MapPost("/alarms/bulk", async (HttpContext http, BulkAlarmActionRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (request.AlarmIds is null || request.AlarmIds.Count is < 1 or > 500) return Results.BadRequest(new { error = "alarmIds must contain 1..500 items." });
    var ids = request.AlarmIds.Distinct().ToArray();
    var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
    if (action is not ("acknowledge" or "resolve" or "assign" or "note" or "workflow")) return Results.BadRequest(new { error = "Unsupported bulk alarm action." });
    var actor = ManagementAuthorization.Actor(http, adminKey, legacyAdminKeyEnabled);
    var updated = new List<AlarmDto>();
    foreach (var id in ids)
    {
        AlarmRecord? alarm = action switch
        {
            "acknowledge" => await store.AcknowledgeAlarmAsync(id, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false),
            "resolve" => await store.ResolveAlarmByIdAsync(id, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false),
            _ => await store.UpdateAlarmWorkflowAsync(id, request.AssignedTo, request.Note, request.DueAtUtc, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false)
        };
        if (alarm is not null) updated.Add(ToAlarmDto(alarm));
    }
    return Results.Ok(updated);
});

securityAdmin.MapGet("/api-tokens", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var tokens = await store.GetManagementApiTokensAsync(ct).ConfigureAwait(false);
    return Results.Ok(tokens.Select(t => new ManagementApiTokenDto(t.TokenId, t.Name, t.Roles, t.CreatedAtUtc, t.ExpiresAtUtc, t.LastUsedAtUtc, t.Revoked)));
});

securityAdmin.MapPost("/api-tokens", async (HttpContext http, CreateManagementApiTokenRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ManagementAuthorization.IsInteractiveAdministrator(http.User)) return Results.Forbid();
    try
    {
        var validity = TimeSpan.FromHours(Math.Clamp(request.ValidForHours, 1, 2160));
        var issued = await store.CreateManagementApiTokenAsync(request.Name, request.Roles, validity, ct).ConfigureAwait(false);
        return Results.Created($"/api/v1/admin/api-tokens/{issued.Record.TokenId}", new CreateManagementApiTokenResponse(issued.Record.TokenId, issued.PlaintextToken, issued.Record.Name, issued.Record.Roles, issued.Record.ExpiresAtUtc));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

securityAdmin.MapDelete("/api-tokens/{tokenId:guid}", async (Guid tokenId, HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
{
    if (!ManagementAuthorization.IsInteractiveAdministrator(http.User)) return Results.Forbid();
    return await store.RevokeManagementApiTokenAsync(tokenId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
});

securityAdmin.MapPost("/users/{userId:guid}/break-glass-codes", async (Guid userId, HttpContext http, CreateBreakGlassCodesRequest request, IControlPlaneStore store, CancellationToken ct) =>
{
    var currentUserId = ManagementAuthorization.GetUserId(http.User);
    if (!ManagementAuthorization.IsInteractiveAdministrator(http.User) || currentUserId != userId) return Results.Forbid();
    try
    {
        var count = Math.Clamp(request.Count, 1, 20);
        var days = Math.Clamp(request.ValidForDays, 1, 90);
        var codes = await store.CreateBreakGlassRecoveryCodesAsync(userId, count, TimeSpan.FromDays(days), ct).ConfigureAwait(false);
        return Results.Ok(new CreateBreakGlassCodesResponse(DateTimeOffset.UtcNow.AddDays(days), codes));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
});

readAdmin.MapGet("/cluster", async (IControlPlaneStore store, CancellationToken ct) =>
{
    var nodeId = Environment.GetEnvironmentVariable("YAZMABACKUP_NODE_ID") ?? Environment.MachineName;
    var lease = await store.GetClusterLeaseAsync("policy-scheduler", ct).ConfigureAwait(false);
    var isLeader = lease is not null && lease.ExpiresAtUtc > DateTimeOffset.UtcNow && string.Equals(lease.OwnerId, nodeId, StringComparison.Ordinal);
    return Results.Ok(new ClusterStatusDto(nodeId, isLeader, lease?.OwnerId, lease?.Epoch, lease?.ExpiresAtUtc, DateTimeOffset.UtcNow));
});


readAdmin.MapGet("/repository-health", async (int? limit, IResilienceStore store, CancellationToken ct) =>
{
    var records = await store.GetRepositoryHealthAsync(Math.Clamp(limit ?? 500, 1, 5000), ct).ConfigureAwait(false);
    return Results.Ok(records.Select(x => new RepositoryHealthDto(x.HealthId, x.PolicyId, x.AgentId, x.RepositoryId, x.RepositoryRoot, x.MeasuredAtUtc, x.TotalBytes, x.FreeBytes, x.RepositoryPhysicalBytes, x.RestorePointCount, x.LatestRestorePointUtc, x.NewBytesLast7Days, x.DailyGrowthBytes, x.EstimatedDaysToFull, x.Status, x.Reason)));
});

backupAdmin.MapPost("/agents/{agentId:guid}/repository-health", async (Guid agentId, HttpContext http, RepositoryHealthScanPayload request, IControlPlaneStore store, CancellationToken ct) =>
{
    if (request.PolicyId == Guid.Empty || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128)) return Results.BadRequest(new { error = "Repository health request is invalid." });
    return await EnqueueAsync(agentId, AgentCommandType.RepositoryHealthScan, request, http, store, ct).ConfigureAwait(false);
});

backupAdmin.MapPost("/recovery-plans", async (CreateRecoveryPlanRequest request, IResilienceStore store, CancellationToken ct) =>
{
    try
    {
        var plan = await store.CreateRecoveryPlanAsync(request.Name, request.PolicyIds, request.MaxParallelAgents, request.RtoTargetMinutes, request.IntervalDays, request.Enabled, ct).ConfigureAwait(false);
        return Results.Created($"/api/v1/admin/recovery-plans/{plan.PlanId}", new RecoveryPlanDto(plan.PlanId, plan.Name, plan.PolicyIds, plan.MaxParallelAgents, plan.RtoTargetMinutes, plan.IntervalDays, plan.Enabled, plan.CreatedAtUtc, plan.LastRunAtUtc, plan.NextRunAtUtc));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

readAdmin.MapGet("/recovery-plans", async (IResilienceStore store, CancellationToken ct) =>
{
    var plans = await store.GetRecoveryPlansAsync(ct).ConfigureAwait(false);
    return Results.Ok(plans.Select(x => new RecoveryPlanDto(x.PlanId, x.Name, x.PolicyIds, x.MaxParallelAgents, x.RtoTargetMinutes, x.IntervalDays, x.Enabled, x.CreatedAtUtc, x.LastRunAtUtc, x.NextRunAtUtc)));
});

readAdmin.MapGet("/recovery-runs", async (int? limit, IResilienceStore store, CancellationToken ct) =>
{
    var runs = await store.GetRecoveryRunsAsync(Math.Clamp(limit ?? 100, 1, 1000), ct).ConfigureAwait(false);
    return Results.Ok(runs.Select(x => new RecoveryRunDto(x.RunId, x.PlanId, x.StartedAtUtc, x.CompletedAtUtc, x.Status, x.RtoTargetMinutes, x.VerifiedBytes, x.LongestRestoreMilliseconds,
        x.Targets.Select(t => new RecoveryRunTargetDto(t.TargetId, t.PolicyId, t.AgentId, t.CommandId, t.Status, t.Succeeded, t.VerifiedBytes, t.DurationMilliseconds, t.Error)).ToArray())));
});


backupAdmin.MapPost("/recovery-runbooks", async (CreateRecoveryRunbookRequest request, IProductionFabricStore store, CancellationToken ct) =>
{
    try
    {
        var runbook = await store.CreateRecoveryRunbookAsync(request.Name, request.RecoveryPlanIds, request.RtoBudgetMinutes, request.IntervalDays, request.Enabled, ct).ConfigureAwait(false);
        return Results.Created($"/api/v1/admin/recovery-runbooks/{runbook.RunbookId}", ToRecoveryRunbookDto(runbook));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

readAdmin.MapGet("/recovery-runbooks", async (IProductionFabricStore store, CancellationToken ct) =>
{
    var items = await store.GetRecoveryRunbooksAsync(ct).ConfigureAwait(false);
    return Results.Ok(items.Select(ToRecoveryRunbookDto));
});

readAdmin.MapGet("/recovery-runbook-runs", async (int? limit, IProductionFabricStore store, CancellationToken ct) =>
{
    var items = await store.GetRecoveryRunbookRunsAsync(Math.Clamp(limit ?? 100, 1, 1000), ct).ConfigureAwait(false);
    return Results.Ok(items.Select(ToRecoveryRunbookRunDto));
});

readAdmin.MapGet("/recovery-runbook-runs/{runId:guid}/evidence", async (Guid runId, RecoveryEvidenceService evidence, CancellationToken ct) =>
{
    var envelope = await evidence.BuildAsync(runId, ct).ConfigureAwait(false);
    return envelope is null ? Results.NotFound() : Results.Ok(envelope);
});

securityAdmin.MapPost("/integration-credentials", async (CreateIntegrationCredentialRequest request, HttpContext http, IProductionFabricStore store, CancellationToken ct) =>
{
    if (!string.Equals(http.User.Identity?.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal)) return Results.Forbid();
    try
    {
        var days = Math.Clamp(request.ValidForDays, 1, 365);
        var created = await store.CreateIntegrationCredentialAsync(request.Name, request.Purpose, TimeSpan.FromDays(days), ct).ConfigureAwait(false);
        return Results.Ok(new CreateIntegrationCredentialResponse(created.Record.CredentialId, created.PlaintextToken, created.Record.Name, created.Record.Purpose, created.Record.ExpiresAtUtc));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

securityAdmin.MapGet("/integration-credentials", async (IProductionFabricStore store, CancellationToken ct) =>
{
    var items = await store.GetIntegrationCredentialsAsync(ct).ConfigureAwait(false);
    return Results.Ok(items.Select(x => new IntegrationCredentialDto(x.CredentialId, x.Name, x.Purpose, x.CreatedAtUtc, x.ExpiresAtUtc, x.LastUsedAtUtc, x.Revoked)));
});

securityAdmin.MapDelete("/integration-credentials/{credentialId:guid}", async (Guid credentialId, HttpContext http, IProductionFabricStore store, CancellationToken ct) =>
{
    if (!string.Equals(http.User.Identity?.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal)) return Results.Forbid();
    return await store.RevokeIntegrationCredentialAsync(credentialId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
});


readAdmin.MapGet("/meshcentral/connector", async (IMeshCentralFleetStore store, CancellationToken ct) =>
{
    var connector = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
    return connector is null ? Results.NoContent() : Results.Ok(ToMeshConnectorDto(connector));
});

securityAdmin.MapPut("/meshcentral/connector", async (SaveMeshCentralConnectorRequest request, MeshCentralConnectorService connectorService, IMeshCentralFleetStore store, CancellationToken ct) =>
{
    if (!Uri.TryCreate(request.BaseUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        return Results.BadRequest(new { error = "MeshCentral adresi mutlak HTTPS URI olmalıdır." });
    if (!ValidText(request.Name, 100) || !ValidText(request.Username, 256)) return Results.BadRequest(new { error = "Connector adı veya kullanıcı adı geçersiz." });
    var mode = (request.AuthenticationMode ?? string.Empty).Trim().ToLowerInvariant();
    if (mode is not ("password" or "loginkey")) return Results.BadRequest(new { error = "AuthenticationMode password veya loginkey olmalıdır." });
    if (request.SyncIntervalMinutes is < 1 or > 1440) return Results.BadRequest(new { error = "Senkronizasyon aralığı 1-1440 dakika olmalıdır." });
    var existing = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
    var protectedCredential = !string.IsNullOrWhiteSpace(request.Credential)
        ? connectorService.ProtectCredential(request.Credential)
        : existing?.ProtectedCredential;
    if (string.IsNullOrWhiteSpace(protectedCredential)) return Results.BadRequest(new { error = "İlk kurulumda MeshCentral kimlik bilgisi gereklidir." });
    var now = DateTimeOffset.UtcNow;
    var record = new MeshCentralConnectorRecord(existing?.ConnectorId ?? Guid.NewGuid(), request.Name.Trim(), uri.GetLeftPart(UriPartial.Authority), request.Username.Trim(), mode, protectedCredential, string.IsNullOrWhiteSpace(request.MeshCtrlPath) ? existing?.MeshCtrlPath : Path.GetFullPath(request.MeshCtrlPath), request.Enabled, request.SyncIntervalMinutes, existing?.CreatedAtUtc ?? now, now, existing?.LastConnectionTestAtUtc, existing?.LastConnectionTestSucceeded, existing?.LastConnectionTestMessage, existing?.LastInventorySyncAtUtc, existing?.ServerVersion);
    var saved = await store.SaveMeshCentralConnectorAsync(record, ct).ConfigureAwait(false);
    return Results.Ok(ToMeshConnectorDto(saved));
});

operateAdmin.MapPost("/meshcentral/test", async (MeshCentralConnectorService connectorService, IMeshCentralFleetStore store, CancellationToken ct) =>
{
    var connector = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
    if (connector is null) return Results.BadRequest(new { error = "Önce MeshCentral connector ayarlarını kaydedin." });
    var result = await connectorService.TestAsync(connector, ct).ConfigureAwait(false);
    var updated = connector with { LastConnectionTestAtUtc = DateTimeOffset.UtcNow, LastConnectionTestSucceeded = result.Succeeded, LastConnectionTestMessage = result.Message, ServerVersion = result.ServerVersion ?? connector.ServerVersion, MeshCtrlPath = result.MeshCtrlPath ?? connector.MeshCtrlPath, UpdatedAtUtc = DateTimeOffset.UtcNow };
    await store.SaveMeshCentralConnectorAsync(updated, ct).ConfigureAwait(false);
    return Results.Ok(new MeshCentralConnectionTestDto(result.Succeeded, result.Message, result.ServerVersion, result.MeshCtrlPath));
});

operateAdmin.MapPost("/meshcentral/synchronize", async (MeshCentralConnectorService connectorService, IMeshCentralFleetStore store, CancellationToken ct) =>
{
    var connector = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
    if (connector is null) return Results.BadRequest(new { error = "Önce MeshCentral connector ayarlarını kaydedin." });
    try
    {
        var devices = await connectorService.SynchronizeAsync(connector, ct).ConfigureAwait(false);
        return Results.Ok(new { synchronized = devices.Count, atUtc = DateTimeOffset.UtcNow });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

readAdmin.MapGet("/meshcentral/fleet", async (IMeshCentralFleetStore store, CancellationToken ct) =>
{
    var devices = await store.GetMeshCentralInventoryAsync(ct).ConfigureAwait(false);
    return Results.Ok(devices.Select(ToMeshDeviceDto));
});

readAdmin.MapGet("/meshcentral/fleet-summary", async (IMeshCentralFleetStore store, CancellationToken ct) =>
{
    var x = await store.GetMeshCentralFleetSummaryAsync(ct).ConfigureAwait(false);
    return Results.Ok(new MeshCentralFleetSummaryDto(x.TotalDevices, x.OnlineDevices, x.MatchedAgents, x.MissingAgents, x.AmbiguousMatches, x.PendingDeployments, x.LastInventorySyncAtUtc));
});

readAdmin.MapGet("/meshcentral/deployments", async (int? limit, IMeshCentralFleetStore store, CancellationToken ct) =>
{
    var rows = await store.GetMeshCentralDeploymentsAsync(limit ?? 200, ct).ConfigureAwait(false);
    return Results.Ok(rows.Select(ToMeshDeploymentDto));
});

operateAdmin.MapPost("/meshcentral/deploy", async (DeployMeshCentralAgentsRequest request, IMeshCentralFleetStore store, MeshCentralDeploymentHostedService deploymentWorker, CancellationToken ct) =>
{
    var connector = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
    if (connector is null || !connector.Enabled) return Results.BadRequest(new { error = "MeshCentral connector yapılandırılmamış veya devre dışı." });
    var publicBase = Environment.GetEnvironmentVariable("YAZMABACKUP_PUBLIC_BASE_URI");
    if (!Uri.TryCreate(publicBase, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps)
        return Results.BadRequest(new { error = "Tek tık dağıtım için YAZMABACKUP_PUBLIC_BASE_URI HTTPS adresi tanımlanmalıdır." });
    var packagePath = Environment.GetEnvironmentVariable("YAZMABACKUP_AGENT_PACKAGE_ZIP");
    if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        return Results.BadRequest(new { error = "YAZMABACKUP_AGENT_PACKAGE_ZIP mevcut bir Agent ZIP paketini göstermelidir." });

    var inventory = await store.GetMeshCentralInventoryAsync(ct).ConfigureAwait(false);
    var nodeIds = (request.NodeIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Take(500).ToArray();
    if (nodeIds.Length == 0) return Results.BadRequest(new { error = "En az bir cihaz seçin." });

    var results = new List<MeshCentralDeploymentDto>();
    foreach (var nodeId in nodeIds)
    {
        var device = inventory.FirstOrDefault(x => x.NodeId == nodeId);
        if (device is null || !device.Online) continue;
        var now = DateTimeOffset.UtcNow;
        var deployment = await store.AddMeshCentralDeploymentAsync(new MeshCentralDeploymentRecord(
            Guid.NewGuid(), connector.ConnectorId, device.NodeId, device.Name, device.MatchedAgentId,
            "queued", now, now, "MeshCentral remote bootstrap accepted into background deployment queue.", null), ct).ConfigureAwait(false);
        deploymentWorker.Signal(deployment.DeploymentId);
        results.Add(ToMeshDeploymentDto(deployment));
    }

    if (results.Count == 0) return Results.BadRequest(new { error = "Seçilen cihazların hiçbiri çevrimiçi MeshCentral hedefi değil." });
    return Results.Accepted(value: results);
});

readAdmin.MapGet("/meshcentral-sync-events", async (int? limit, IProductionFabricStore store, CancellationToken ct) =>
{
    var events = await store.GetMeshCentralSyncEventsAsync(Math.Clamp(limit ?? 200, 1, 5000), ct).ConfigureAwait(false);
    return Results.Ok(events.Select(x => new MeshCentralSyncEventDto(x.EventId, x.AgentId, x.NodeId, x.NodeStatus, x.DeploymentStatus, x.ReportedAtUtc, x.ReceivedAtUtc, x.CredentialId)));
});

app.MapPost("/api/v1/integrations/meshcentral/status", async (HttpContext http, MeshCentralStatusReportRequest request, IProductionFabricStore store, IControlPlaneStore auditStore, CancellationToken ct) =>
{
    var authorization = http.Request.Headers.Authorization.FirstOrDefault();
    const string prefix = "Bearer ";
    if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
    var token = authorization[prefix.Length..].Trim();
    var credential = await store.AuthenticateIntegrationCredentialAsync(token, "meshcentral-status", ct).ConfigureAwait(false);
    if (credential is null) return Results.Unauthorized();
    try
    {
        var received = DateTimeOffset.UtcNow;
        var record = await store.ReportMeshCentralSyncAsync(credential.CredentialId, request.EventId, request.AgentId, request.NodeId, request.NodeStatus, request.DeploymentStatus, request.ReportedAtUtc, received, ct).ConfigureAwait(false);
        await auditStore.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), received, $"integration:{credential.Name}", "meshcentral-status", "POST", "/api/v1/integrations/meshcentral/status", 200, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
        return Results.Ok(new MeshCentralSyncEventDto(record.EventId, record.AgentId, record.NodeId, record.NodeStatus, record.DeploymentStatus, record.ReportedAtUtc, record.ReceivedAtUtc, record.CredentialId));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).RequireRateLimiting("integration");

securityAdmin.MapPost("/agents/{agentId:guid}/meshcentral-link", async (Guid agentId, LinkMeshCentralNodeRequest request, IResilienceStore store, CancellationToken ct) =>
{
    try
    {
        var link = await store.UpsertMeshCentralLinkAsync(agentId, request.MeshCentralBaseUri, request.NodeId, ct).ConfigureAwait(false);
        return Results.Ok(new MeshCentralLinkDto(link.AgentId, link.MeshCentralBaseUri, link.NodeId, link.LinkedAtUtc, link.LastSynchronizedAtUtc, link.LastKnownNodeStatus, link.LastDeploymentStatus));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

readAdmin.MapGet("/meshcentral-links", async (IResilienceStore store, CancellationToken ct) =>
{
    var links = await store.GetMeshCentralLinksAsync(ct).ConfigureAwait(false);
    return Results.Ok(links.Select(x => new MeshCentralLinkDto(x.AgentId, x.MeshCentralBaseUri, x.NodeId, x.LinkedAtUtc, x.LastSynchronizedAtUtc, x.LastKnownNodeStatus, x.LastDeploymentStatus)));
});

securityAdmin.MapPost("/notification-routes", async (CreateNotificationRouteRequest request, IDataProtectionProvider dataProtection, IResilienceStore store, CancellationToken ct) =>
{
    if (!ValidText(request.Name, 128) || !Uri.TryCreate(request.Destination, UriKind.Absolute, out var destination) || destination.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(destination.UserInfo))
        return Results.BadRequest(new { error = "Notification route name or HTTPS destination is invalid." });
    var secret = request.HmacSecret ?? string.Empty;
    if (secret.Length is < 32 or > 256 || secret.Any(char.IsControl)) return Results.BadRequest(new { error = "HMAC secret must be 32..256 characters." });
    var allowedHosts = (Environment.GetEnvironmentVariable("YAZMABACKUP_NOTIFICATION_ALLOWED_HOSTS") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (!allowedHosts.Contains(destination.IdnHost, StringComparer.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "Notification destination host is not allowlisted." });
    var severities = (request.Severities ?? [AlarmSeverity.Critical, AlarmSeverity.Warning]).Where(AlarmSeverity.All.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (severities.Length == 0) return Results.BadRequest(new { error = "At least one valid severity is required." });
    var protector = dataProtection.CreateProtector("YazmaBackup.NotificationHmac.v1");
    var route = new NotificationRouteRecord(Guid.NewGuid(), request.Name.Trim(), destination.ToString(), protector.Protect(secret), severities, request.Enabled, DateTimeOffset.UtcNow);
    secret = string.Empty;
    var created = await store.CreateNotificationRouteAsync(route, ct).ConfigureAwait(false);
    return Results.Created($"/api/v1/admin/notification-routes/{created.RouteId}", new NotificationRouteDto(created.RouteId, created.Name, created.Destination, created.Severities, created.Enabled, created.CreatedAtUtc));
});

securityAdmin.MapGet("/notification-routes", async (IResilienceStore store, CancellationToken ct) =>
{
    var routes = await store.GetNotificationRoutesAsync(ct).ConfigureAwait(false);
    return Results.Ok(routes.Select(x => new NotificationRouteDto(x.RouteId, x.Name, x.Destination, x.Severities, x.Enabled, x.CreatedAtUtc)));
});

securityAdmin.MapDelete("/notification-routes/{routeId:guid}", async (Guid routeId, IResilienceStore store, CancellationToken ct) =>
    await store.DeleteNotificationRouteAsync(routeId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound());

securityAdmin.MapGet("/notification-deliveries", async (int? limit, IResilienceStore store, CancellationToken ct) =>
{
    var deliveries = await store.GetNotificationDeliveriesAsync(Math.Clamp(limit ?? 200, 1, 1000), ct).ConfigureAwait(false);
    return Results.Ok(deliveries.Select(x => new NotificationDeliveryDto(x.DeliveryId, x.RouteId, x.AlarmId, x.AlarmVersionUtc, x.CreatedAtUtc, x.CompletedAtUtc, x.AttemptCount, x.Succeeded, x.Error)));
});

app.MapFallbackToFile("index.html");
app.Run();



static RecoveryRunbookDto ToRecoveryRunbookDto(RecoveryRunbookRecord x) =>
    new(x.RunbookId, x.Name, x.RecoveryPlanIds, x.RtoBudgetMinutes, x.IntervalDays, x.Enabled, x.CreatedAtUtc, x.LastRunAtUtc, x.NextRunAtUtc);

static RecoveryRunbookRunDto ToRecoveryRunbookRunDto(RecoveryRunbookRunRecord x) =>
    new(x.RunId, x.RunbookId, x.StartedAtUtc, x.CompletedAtUtc, x.Status, x.RtoBudgetMinutes, x.CurrentStepIndex,
        x.Steps.Select(s => new RecoveryRunbookStepDto(s.StepIndex, s.RecoveryPlanId, s.RecoveryRunId, s.Status, s.StartedAtUtc, s.CompletedAtUtc, s.Error)).ToArray());

static async Task<IResult> EnqueueAsync<TPayload>(Guid agentId, AgentCommandType type, TPayload payload, HttpContext http, IControlPlaneStore store, CancellationToken ct)
{
    var idempotencyKey = http.Request.Headers["X-YazmaBackup-Idempotency-Key"].FirstOrDefault();
    try
    {
        var command = await store.EnqueueAsync(agentId, type, JsonSerializer.Serialize(payload), idempotencyKey, ct).ConfigureAwait(false);
        return Results.Accepted($"/api/v1/admin/commands/{command.CommandId}", new EnqueueCommandResponse(command.CommandId));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}

static async Task<(Guid AgentId, AgentRecord Record)?> AuthenticateAgentAsync(HttpContext http, IControlPlaneStore store, CancellationToken ct)
{
    if (!Guid.TryParse(http.Request.Headers["X-YazmaBackup-Agent-Id"].FirstOrDefault(), out var agentId)) return null;
    var token = http.Request.Headers.Authorization.FirstOrDefault();
    const string prefix = "Bearer ";
    if (token is null || !token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
    var record = await store.AuthenticateAsync(agentId, token[prefix.Length..], ct).ConfigureAwait(false);
    return record is null ? null : (agentId, record);
}

static bool ValidBackupRequest(string path, string repositoryRoot, string repositoryId, long activeRate, long idleRate, int idleThresholdSeconds, RetentionPolicy retention, ProtectionPolicy protection, out string? error)
{
    error = null;
    if (!ValidPathInput(path) || !ValidPathInput(repositoryRoot) || !ValidIdentifier(repositoryId, 128))
    {
        error = "Path, repositoryRoot or repositoryId is invalid.";
        return false;
    }
    if (activeRate is < 0 or > 1024L * 1024 * 1024 || idleRate is < 0 or > 1024L * 1024 * 1024)
    {
        error = "Bandwidth limits must be between 0 and 1 GiB/s. 0 means unlimited.";
        return false;
    }
    if (idleThresholdSeconds is < 30 or > 86400)
    {
        error = "User idle threshold must be 30..86400 seconds.";
        return false;
    }
    try
    {
        retention.Validate();
        protection.Validate();
    }
    catch (ArgumentOutOfRangeException ex)
    {
        error = ex.Message;
        return false;
    }
    return true;
}


static MeshCentralConnectorDto ToMeshConnectorDto(MeshCentralConnectorRecord x) =>
    new(x.ConnectorId, x.Name, x.BaseUri, x.Username, x.AuthenticationMode, !string.IsNullOrWhiteSpace(x.ProtectedCredential), x.MeshCtrlPath, x.Enabled, x.SyncIntervalMinutes, x.LastConnectionTestAtUtc, x.LastConnectionTestSucceeded, x.LastConnectionTestMessage, x.LastInventorySyncAtUtc, x.ServerVersion);
static MeshCentralInventoryDeviceDto ToMeshDeviceDto(MeshCentralInventoryDeviceRecord x) =>
    new(x.NodeId, x.Name, x.Hostname, x.Domain, x.GroupName, x.Online, x.ObservedAtUtc, x.MatchedAgentId, x.MatchStatus, x.MatchEvidence, x.AgentVersion);
static MeshCentralDeploymentDto ToMeshDeploymentDto(MeshCentralDeploymentRecord x) =>
    new(x.DeploymentId, x.NodeId, x.DeviceName, x.AgentId, x.Status, x.RequestedAtUtc, x.UpdatedAtUtc, x.Detail, x.CommandId);

static AlarmDto ToAlarmDto(AlarmRecord a) =>
    new(a.AlarmId, a.Severity, a.Category, a.Title, a.Details, a.Status, a.AgentId, a.FirstSeenAtUtc, a.LastSeenAtUtc, a.AcknowledgedAtUtc, a.AcknowledgedBy, a.ResolvedAtUtc, a.AssignedTo, a.OperatorNote, a.DueAtUtc, a.WorkflowUpdatedAtUtc);

static BackupPolicyDto ToPolicyDto(BackupPolicyRecord p) =>
    new(p.PolicyId, p.Name, p.AgentId, p.SourcePath, p.RepositoryRoot, p.RepositoryId, p.RequireSnapshot, p.IntervalMinutes,
        p.ActiveBytesPerSecond, p.IdleBytesPerSecond, p.UserIdleThresholdSeconds, p.Retention, p.Protection ?? new ProtectionPolicy(), p.Enabled, p.LastScheduledAtUtc, p.NextRunAtUtc,
        p.RestoreDrillIntervalDays, p.LastRestoreDrillScheduledAtUtc, p.NextRestoreDrillAtUtc,
        p.RepositoryHealthIntervalHours, p.LastRepositoryHealthScheduledAtUtc, p.NextRepositoryHealthAtUtc);

static SessionUserDto ToSessionUser(ManagementUserRecord user) =>
    new(user.UserId, user.Username, user.DisplayName, user.Roles, user.MustChangePassword);

static ManagementUserDto ToManagementUserDto(ManagementUserRecord user) =>
    new(user.UserId, user.Username, user.DisplayName, user.Roles, user.Enabled, user.MustChangePassword, user.CreatedAtUtc, user.LastLoginAtUtc, user.LockedUntilUtc);

static bool ValidCsrf(HttpContext http)
{
    var cookie = http.Request.Cookies["yb-csrf"];
    var header = http.Request.Headers["X-YazmaBackup-CSRF"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(cookie) && !string.IsNullOrWhiteSpace(header) && Security.ConstantTimeEquals(header, cookie);
}

static bool ValidMachine(string? value) => ValidText(value, 128);
static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
static string? ClassifyBackupError(string? error)
{
    if (string.IsNullOrWhiteSpace(error)) return null;
    if (error.Contains("SMB connection", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("network path", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("network name", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Win32Exception", StringComparison.OrdinalIgnoreCase))
        return "NAS/SMB";
    if (error.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        return "Erişim İzni";
    if (error.Contains("repository key", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("encryption", StringComparison.OrdinalIgnoreCase))
        return "Şifreleme Anahtarı";
    if (error.Contains("snapshot", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("VSS", StringComparison.OrdinalIgnoreCase))
        return "VSS/Snapshot";
    if (error.Contains("DirectoryNotFound", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase))
        return "Dosya/Klasör";
    if (error.Contains("protection", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("ransomware", StringComparison.OrdinalIgnoreCase))
        return "Koruma";
    return "Agent";
}

static bool ValidPathInput(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 32767;
static bool ValidIdentifier(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
static bool ValidSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
static bool ValidOptionalPublicKey(string? pem) => string.IsNullOrWhiteSpace(pem) || ValidPublicKey(pem);
static bool ValidPublicKey(string? pem)
{
    if (string.IsNullOrWhiteSpace(pem) || pem.Length > 8192) return false;
    try
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa.KeySize >= 3072;
    }
    catch (CryptographicException) { return false; }
    catch (ArgumentException) { return false; }
}
static string[] SanitizeCapabilities(IReadOnlyList<string>? capabilities) => (capabilities ?? [])
    .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 64)
    .Select(x => x.Trim())
    .Distinct(StringComparer.Ordinal)
    .OrderBy(x => x, StringComparer.Ordinal)
    .ToArray();

static IReadOnlyList<RepositoryCircuitTelemetry>? SanitizeRepositoryCircuits(IReadOnlyList<RepositoryCircuitTelemetry>? circuits)
{
    if (circuits is null) return null;
    return circuits.Where(x => ValidIdentifier(x.RepositoryId, 128))
        .Take(128)
        .Select(x => new RepositoryCircuitTelemetry(x.RepositoryId, Math.Clamp(x.ConsecutiveFailures, 0, 100000), x.LastFailureAtUtc, x.OpenUntilUtc, string.IsNullOrWhiteSpace(x.LastError) ? null : x.LastError.Length <= 256 ? x.LastError : x.LastError[..256]))
        .ToArray();
}

static ProtectionTelemetryDto? SanitizeProtection(ProtectionTelemetryDto? protection)
{
    if (protection is null) return null;
    var status = protection.Status?.Trim().ToLowerInvariant();
    if (status is not ("healthy" or "locked")) return null;
    var reason = string.IsNullOrWhiteSpace(protection.Reason) ? null : protection.Reason.Trim();
    if (reason?.Length > 256) reason = reason[..256];
    return new ProtectionTelemetryDto(status, reason, protection.TriggeredAtUtc, protection.IncidentId);
}
