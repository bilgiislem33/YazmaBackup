using System.Runtime.Versioning;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.Agent;

public sealed record ProtectionLockState(Guid IncidentId, DateTimeOffset TriggeredAtUtc, string Reason, ProtectionAssessment Assessment);

[SupportedOSPlatform("windows")]
public sealed class ProtectionLockStore
{
    private readonly MachineSecretStore _secrets = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ProtectionLockState? Read()
    {
        var json = _secrets.Read(AgentPaths.ProtectionLockFile);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ProtectionLockState>(json, JsonOptions)
                ?? throw new InvalidDataException("Protection lock state is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Protection lock state could not be parsed.", ex);
        }
    }

    public ProtectionLockState Lock(ProtectionAssessment assessment)
    {
        var existing = Read();
        if (existing is not null) return existing;
        var state = new ProtectionLockState(Guid.NewGuid(), DateTimeOffset.UtcNow, assessment.Reason, assessment);
        _secrets.Write(AgentPaths.ProtectionLockFile, JsonSerializer.Serialize(state, JsonOptions));
        return state;
    }

    public bool Clear(Guid? expectedIncidentId)
    {
        var existing = Read();
        if (existing is null) return false;
        if (expectedIncidentId is not null && expectedIncidentId != existing.IncidentId)
            throw new InvalidOperationException("Protection incident id does not match the active lock.");
        MachineSecretStore.Delete(AgentPaths.ProtectionLockFile);
        return true;
    }

    public ProtectionTelemetryDto Telemetry()
    {
        var state = Read();
        return state is null
            ? new ProtectionTelemetryDto("healthy", null, null, null)
            : new ProtectionTelemetryDto("locked", state.Reason, state.TriggeredAtUtc, state.IncidentId);
    }
}
