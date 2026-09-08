using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class EndpointSupport
{
    internal static RecoveryRunbookDto ToRecoveryRunbookDto(RecoveryRunbookRecord x) =>
        new(x.RunbookId, x.Name, x.RecoveryPlanIds, x.RtoBudgetMinutes, x.IntervalDays, x.Enabled, x.CreatedAtUtc, x.LastRunAtUtc, x.NextRunAtUtc);

    internal static RecoveryRunbookRunDto ToRecoveryRunbookRunDto(RecoveryRunbookRunRecord x) =>
        new(x.RunId, x.RunbookId, x.StartedAtUtc, x.CompletedAtUtc, x.Status, x.RtoBudgetMinutes, x.CurrentStepIndex,
            x.Steps.Select(s => new RecoveryRunbookStepDto(s.StepIndex, s.RecoveryPlanId, s.RecoveryRunId, s.Status, s.StartedAtUtc, s.CompletedAtUtc, s.Error)).ToArray());

    internal static bool ValidBackupRequest(string path, string repositoryRoot, string repositoryId, long activeRate, long idleRate, int idleThresholdSeconds, RetentionPolicy retention, ProtectionPolicy protection, out string? error)
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


    internal static MeshCentralConnectorDto ToMeshConnectorDto(MeshCentralConnectorRecord x) =>
        new(x.ConnectorId, x.Name, x.BaseUri, x.Username, x.AuthenticationMode, !string.IsNullOrWhiteSpace(x.ProtectedCredential), x.MeshCtrlPath, x.Enabled, x.SyncIntervalMinutes, x.LastConnectionTestAtUtc, x.LastConnectionTestSucceeded, x.LastConnectionTestMessage, x.LastInventorySyncAtUtc, x.ServerVersion);
    internal static MeshCentralInventoryDeviceDto ToMeshDeviceDto(MeshCentralInventoryDeviceRecord x) =>
        new(x.NodeId, x.Name, x.Hostname, x.Domain, x.GroupName, x.Online, x.ObservedAtUtc, x.MatchedAgentId, x.MatchStatus, x.MatchEvidence, x.AgentVersion);
    internal static MeshCentralDeploymentDto ToMeshDeploymentDto(MeshCentralDeploymentRecord x) =>
        new(x.DeploymentId, x.NodeId, x.DeviceName, x.AgentId, x.Status, x.RequestedAtUtc, x.UpdatedAtUtc, x.Detail, x.CommandId);

    internal static AlarmDto ToAlarmDto(AlarmRecord a) =>
        new(a.AlarmId, a.Severity, a.Category, a.Title, a.Details, a.Status, a.AgentId, a.FirstSeenAtUtc, a.LastSeenAtUtc, a.AcknowledgedAtUtc, a.AcknowledgedBy, a.ResolvedAtUtc, a.AssignedTo, a.OperatorNote, a.DueAtUtc, a.WorkflowUpdatedAtUtc);

    internal static BackupPolicyDto ToPolicyDto(BackupPolicyRecord p) =>
        new(p.PolicyId, p.Name, p.AgentId, p.SourcePath, p.RepositoryRoot, p.RepositoryId, p.RequireSnapshot, p.IntervalMinutes,
            p.ActiveBytesPerSecond, p.IdleBytesPerSecond, p.UserIdleThresholdSeconds, p.Retention, p.Protection ?? new ProtectionPolicy(), p.Enabled, p.LastScheduledAtUtc, p.NextRunAtUtc,
            p.RestoreDrillIntervalDays, p.LastRestoreDrillScheduledAtUtc, p.NextRestoreDrillAtUtc,
            p.RepositoryHealthIntervalHours, p.LastRepositoryHealthScheduledAtUtc, p.NextRepositoryHealthAtUtc);

    internal static ManagementUserDto ToManagementUserDto(ManagementUserRecord user) =>
        new(user.UserId, user.Username, user.DisplayName, user.Roles, user.Enabled, user.MustChangePassword, user.CreatedAtUtc, user.LastLoginAtUtc, user.LockedUntilUtc);

    internal static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    internal static string? ClassifyBackupError(string? error)
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

    internal static bool ValidPathInput(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 32767;
    internal static bool ValidIdentifier(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
    internal static bool ValidSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
}
