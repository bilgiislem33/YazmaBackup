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

internal static class ControlPlanePipeline
{
    internal static void UseControlPlanePipeline(this WebApplication app, ControlPlaneHostSettings settings)
    {
        var adminKey = settings.AdminKey;
        var legacyAdminKeyEnabled = settings.LegacyAdminKeyEnabled;
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

            if (isAdminMutation && usingCookieIdentity && !usingLegacyAdminKey && !ManagementRequestSecurity.HasValidCsrf(context.Request))
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


    }
}
