using System.Text.Json;
using YazmaBackup.Domain;
using Npgsql;

namespace YazmaBackup.ControlPlane;

public sealed record TransactionalStateSnapshot(long Version, string Json);

public sealed class PostgreSqlStateEngine
{
    private readonly string _connectionString;
    private readonly string _clusterId;

    public PostgreSqlStateEngine(string connectionString, string clusterId)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
        _clusterId = string.IsNullOrWhiteSpace(clusterId) ? "default" : clusterId.Trim();
        EnsureSchema();
    }

    public string ClusterId => _clusterId;

    public TransactionalStateSnapshot LoadOrBootstrap(string bootstrapJson)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);

        using (var insert = new NpgsqlCommand("""
            INSERT INTO yazmabackup_control_plane_state(cluster_id, version, state_json, updated_at_utc)
            VALUES (@cluster, 0, CAST(@json AS jsonb), now())
            ON CONFLICT (cluster_id) DO NOTHING
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("cluster", _clusterId);
            insert.Parameters.AddWithValue("json", bootstrapJson);
            insert.ExecuteNonQuery();
        }

        using var select = new NpgsqlCommand("SELECT version, state_json::text FROM yazmabackup_control_plane_state WHERE cluster_id=@cluster FOR UPDATE", connection, tx);
        select.Parameters.AddWithValue("cluster", _clusterId);
        using var reader = select.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Transactional Control Plane state bootstrap failed.");
        var result = new TransactionalStateSnapshot(reader.GetInt64(0), reader.GetString(1));
        reader.Close();

        SyncNormalizedProjection(connection, tx, result.Json);
        tx.Commit();
        return result;
    }

    public async Task<TransactionalStateSnapshot?> TryCommitAsync(long expectedVersion, string json, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand("""
            UPDATE yazmabackup_control_plane_state
               SET version = version + 1,
                   state_json = CAST(@json AS jsonb),
                   updated_at_utc = now()
             WHERE cluster_id = @cluster AND version = @expected
         RETURNING version, state_json::text
            """, connection, tx);
        command.Parameters.AddWithValue("cluster", _clusterId);
        command.Parameters.AddWithValue("expected", expectedVersion);
        command.Parameters.AddWithValue("json", json);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        TransactionalStateSnapshot? committed = null;
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            committed = new TransactionalStateSnapshot(reader.GetInt64(0), reader.GetString(1));
        await reader.DisposeAsync().ConfigureAwait(false);

        if (committed is not null)
        {
            await SyncNormalizedProjectionAsync(connection, tx, committed.Json, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return committed;
        }

        await tx.RollbackAsync(ct).ConfigureAwait(false);
        return null;
    }



    public async Task<(AgentCommand? Command, TransactionalStateSnapshot Snapshot)> ClaimNextCommandAsync(
        Guid agentId,
        int maxAttempts,
        TimeSpan leaseDuration,
        TimeSpan completedRetention,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        long stateVersion;
        string stateJson;
        await using (var stateLock = new NpgsqlCommand(
            "SELECT version, state_json::text FROM yazmabackup_control_plane_state WHERE cluster_id=@cluster FOR UPDATE", connection, tx))
        {
            stateLock.Parameters.AddWithValue("cluster", _clusterId);
            await using var reader = await stateLock.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Transactional Control Plane state row is missing.");
            stateVersion = reader.GetInt64(0);
            stateJson = reader.GetString(1);
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.Subtract(completedRetention);
        var changed = false;

        // Retention is executed under the same state-row fence so the compatibility snapshot and normalized queue stay atomic.
        await using (var prune = new NpgsqlCommand("""
            WITH doomed AS (
                SELECT command_id
                  FROM yb_commands
                 WHERE cluster_id=@cluster
                   AND completed_at_utc IS NOT NULL
                   AND completed_at_utc < @cutoff
            ),
            deleted AS (
                DELETE FROM yb_commands c
                 USING doomed d
                 WHERE c.cluster_id=@cluster AND c.command_id=d.command_id
                 RETURNING c.command_id
            )
            SELECT COALESCE(array_agg(command_id::text), ARRAY[]::text[]) FROM deleted
            """, connection, tx))
        {
            prune.Parameters.AddWithValue("cluster", _clusterId);
            prune.Parameters.AddWithValue("cutoff", cutoff);
            var deleted = (string[]?)await prune.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? Array.Empty<string>();
            foreach (var id in deleted)
                stateJson = RemoveCommandFromStateJson(stateJson, id);
            changed |= deleted.Length > 0;
        }

        // Exhaust commands whose previous leases expired and whose delivery budget is consumed.
        var exhausted = new List<AgentCommand>();
        await using (var exhaust = new NpgsqlCommand("""
            SELECT payload::text
              FROM yb_commands
             WHERE cluster_id=@cluster
               AND agent_id=@agent
               AND completed_at_utc IS NULL
               AND attempt_count>=@maxAttempts
               AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc<=@now)
             FOR UPDATE
            """, connection, tx))
        {
            exhaust.Parameters.AddWithValue("cluster", _clusterId);
            exhaust.Parameters.AddWithValue("agent", agentId);
            exhaust.Parameters.AddWithValue("maxAttempts", maxAttempts);
            exhaust.Parameters.AddWithValue("now", now);
            await using var reader = await exhaust.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                exhausted.Add(DeserializePayload<AgentCommand>(reader.GetString(0)));
        }

        foreach (var command in exhausted)
        {
            var failed = command with
            {
                CompletedAtUtc = now,
                Succeeded = false,
                Error = $"Command exhausted after {maxAttempts} delivery attempts.",
                LeaseId = null,
                LeaseExpiresAtUtc = null
            };
            await UpsertCommandRowAsync(connection, tx, failed, ct).ConfigureAwait(false);
            stateJson = SetCommandInStateJson(stateJson, failed);
            changed = true;
        }

        AgentCommand? claimed = null;
        await using (var select = new NpgsqlCommand("""
            SELECT payload::text
              FROM yb_commands
             WHERE cluster_id=@cluster
               AND agent_id=@agent
               AND completed_at_utc IS NULL
               AND attempt_count<@maxAttempts
               AND (lease_id IS NULL OR lease_expires_at_utc IS NULL OR lease_expires_at_utc<=@now)
             ORDER BY created_at_utc
             FOR UPDATE SKIP LOCKED
             LIMIT 1
            """, connection, tx))
        {
            select.Parameters.AddWithValue("cluster", _clusterId);
            select.Parameters.AddWithValue("agent", agentId);
            select.Parameters.AddWithValue("maxAttempts", maxAttempts);
            select.Parameters.AddWithValue("now", now);
            var scalar = await select.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is string json)
            {
                var candidate = DeserializePayload<AgentCommand>(json);
                claimed = candidate with
                {
                    ClaimedAtUtc = now,
                    LeaseId = Guid.NewGuid(),
                    LeaseExpiresAtUtc = now.Add(leaseDuration),
                    LastLeaseRenewalUtc = now,
                    AttemptCount = candidate.AttemptCount + 1
                };
                await UpsertCommandRowAsync(connection, tx, claimed, ct).ConfigureAwait(false);
                stateJson = SetCommandInStateJson(stateJson, claimed);
                changed = true;
            }
        }

        var finalVersion = stateVersion;
        if (changed)
        {
            finalVersion = stateVersion + 1;
            await using var updateState = new NpgsqlCommand("""
                UPDATE yazmabackup_control_plane_state
                   SET version=@version, state_json=CAST(@json AS jsonb), updated_at_utc=now()
                 WHERE cluster_id=@cluster AND version=@expected
                """, connection, tx);
            updateState.Parameters.AddWithValue("version", finalVersion);
            updateState.Parameters.AddWithValue("json", stateJson);
            updateState.Parameters.AddWithValue("cluster", _clusterId);
            updateState.Parameters.AddWithValue("expected", stateVersion);
            if (await updateState.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Command transaction state fence was lost.");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (claimed, new TransactionalStateSnapshot(finalVersion, stateJson));
    }

    public async Task<(DateTimeOffset ExpiresAtUtc, TransactionalStateSnapshot Snapshot)> RenewCommandLeaseAsync(
        Guid agentId,
        Guid commandId,
        Guid leaseId,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        long stateVersion;
        string stateJson;
        await using (var stateLock = new NpgsqlCommand(
            "SELECT version, state_json::text FROM yazmabackup_control_plane_state WHERE cluster_id=@cluster FOR UPDATE", connection, tx))
        {
            stateLock.Parameters.AddWithValue("cluster", _clusterId);
            await using var reader = await stateLock.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Transactional Control Plane state row is missing.");
            stateVersion = reader.GetInt64(0);
            stateJson = reader.GetString(1);
        }

        AgentCommand command;
        await using (var select = new NpgsqlCommand("""
            SELECT payload::text
              FROM yb_commands
             WHERE cluster_id=@cluster AND command_id=@command AND agent_id=@agent
             FOR UPDATE
            """, connection, tx))
        {
            select.Parameters.AddWithValue("cluster", _clusterId);
            select.Parameters.AddWithValue("command", commandId);
            select.Parameters.AddWithValue("agent", agentId);
            var scalar = await select.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is not string json) throw new KeyNotFoundException("Command not found.");
            command = DeserializePayload<AgentCommand>(json);
        }

        if (command.CompletedAtUtc is not null) throw new InvalidOperationException("Command is already completed.");
        if (command.LeaseId != leaseId) throw new InvalidOperationException("Command lease is no longer owned by this execution.");

        var now = DateTimeOffset.UtcNow;
        if (command.LeaseExpiresAtUtc is null || command.LeaseExpiresAtUtc <= now)
            throw new InvalidOperationException("Command lease has expired.");

        var renewed = command with { LastLeaseRenewalUtc = now, LeaseExpiresAtUtc = now.Add(leaseDuration) };
        await UpsertCommandRowAsync(connection, tx, renewed, ct).ConfigureAwait(false);
        stateJson = SetCommandInStateJson(stateJson, renewed);

        await using (var updateState = new NpgsqlCommand("""
            UPDATE yazmabackup_control_plane_state
               SET version=version+1, state_json=CAST(@json AS jsonb), updated_at_utc=now()
             WHERE cluster_id=@cluster AND version=@expected
            """, connection, tx))
        {
            updateState.Parameters.AddWithValue("json", stateJson);
            updateState.Parameters.AddWithValue("cluster", _clusterId);
            updateState.Parameters.AddWithValue("expected", stateVersion);
            if (await updateState.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Command lease renewal state fence was lost.");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (renewed.LeaseExpiresAtUtc!.Value, new TransactionalStateSnapshot(stateVersion + 1, stateJson));
    }

    private async Task UpsertCommandRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        AgentCommand command,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using var upsert = new NpgsqlCommand("""
            INSERT INTO yb_commands(cluster_id, command_id, agent_id, command_type, created_at_utc, claimed_at_utc, lease_id, lease_expires_at_utc, last_lease_renewal_utc, completed_at_utc, succeeded, attempt_count, idempotency_key, error, payload)
            VALUES (@cluster,@command,@agent,@type,@created,@claimed,@leaseId,@leaseExpires,@leaseRenewal,@completed,@succeeded,@attempts,@idempotency,@error,CAST(@payload AS jsonb))
            ON CONFLICT(cluster_id, command_id) DO UPDATE SET
              agent_id=excluded.agent_id, command_type=excluded.command_type, created_at_utc=excluded.created_at_utc,
              claimed_at_utc=excluded.claimed_at_utc, lease_id=excluded.lease_id, lease_expires_at_utc=excluded.lease_expires_at_utc,
              last_lease_renewal_utc=excluded.last_lease_renewal_utc, completed_at_utc=excluded.completed_at_utc,
              succeeded=excluded.succeeded, attempt_count=excluded.attempt_count, idempotency_key=excluded.idempotency_key,
              error=excluded.error, payload=excluded.payload
            """, connection, tx);
        upsert.Parameters.AddWithValue("cluster", _clusterId);
        upsert.Parameters.AddWithValue("command", command.CommandId);
        upsert.Parameters.AddWithValue("agent", command.AgentId);
        upsert.Parameters.AddWithValue("type", ((int)command.Type).ToString(System.Globalization.CultureInfo.InvariantCulture));
        upsert.Parameters.AddWithValue("created", command.CreatedAtUtc);
        upsert.Parameters.AddWithValue("claimed", (object?)command.ClaimedAtUtc ?? DBNull.Value);
        upsert.Parameters.AddWithValue("leaseId", (object?)command.LeaseId ?? DBNull.Value);
        upsert.Parameters.AddWithValue("leaseExpires", (object?)command.LeaseExpiresAtUtc ?? DBNull.Value);
        upsert.Parameters.AddWithValue("leaseRenewal", (object?)command.LastLeaseRenewalUtc ?? DBNull.Value);
        upsert.Parameters.AddWithValue("completed", (object?)command.CompletedAtUtc ?? DBNull.Value);
        upsert.Parameters.AddWithValue("succeeded", command.Succeeded);
        upsert.Parameters.AddWithValue("attempts", command.AttemptCount);
        upsert.Parameters.AddWithValue("idempotency", (object?)command.IdempotencyKey ?? DBNull.Value);
        upsert.Parameters.AddWithValue("error", (object?)command.Error ?? DBNull.Value);
        upsert.Parameters.AddWithValue("payload", json);
        await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string SetCommandInStateJson(string stateJson, AgentCommand command)
    {
        var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stateJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("State JSON is invalid.");
        var commands = state.TryGetValue("commands", out var existing) && existing.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        commands[command.CommandId.ToString()] = JsonSerializer.SerializeToElement(command, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        state["commands"] = JsonSerializer.SerializeToElement(commands, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string RemoveCommandFromStateJson(string stateJson, string commandId)
    {
        var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stateJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("State JSON is invalid.");
        if (!state.TryGetValue("commands", out var existing) || existing.ValueKind != JsonValueKind.Object) return stateJson;
        var commands = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        commands.Remove(commandId);
        state["commands"] = JsonSerializer.SerializeToElement(commands, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }



    public async Task<TransactionalStateSnapshot?> TryCommitMultiAggregateCompletionAsync(
        long expectedVersion,
        string stateJson,
        AgentCommand completedCommand,
        Guid completionLeaseId,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

        // Exact command row ownership is locked before completion is accepted.
        await using (var ownership = new NpgsqlCommand("""
            SELECT lease_id, lease_expires_at_utc, completed_at_utc, completion_lease_id
              FROM yb_commands
             WHERE cluster_id=@cluster AND command_id=@command AND agent_id=@agent
             FOR UPDATE
            """, connection, tx))
        {
            ownership.Parameters.AddWithValue("cluster", _clusterId);
            ownership.Parameters.AddWithValue("command", completedCommand.CommandId);
            ownership.Parameters.AddWithValue("agent", completedCommand.AgentId);
            await using var reader = await ownership.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new KeyNotFoundException("Command not found.");
            if (!reader.IsDBNull(2))
            {
                var priorCompletionLease = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                if (priorCompletionLease == completionLeaseId)
                    return await LoadAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException("Command was already completed by a different execution lease.");
            }

            var databaseLease = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
            var databaseLeaseExpiry = reader.IsDBNull(1) ? (DateTimeOffset?)null : new DateTimeOffset(reader.GetDateTime(1).ToUniversalTime());
            if (databaseLease != completionLeaseId)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException("Command completion rejected: database lease ownership changed.");
            }
            if (databaseLeaseExpiry is null || databaseLeaseExpiry <= DateTimeOffset.UtcNow)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException("Command completion rejected: database lease expired.");
            }
        }

        await using var stateCommand = new NpgsqlCommand("""
            UPDATE yazmabackup_control_plane_state
               SET version = version + 1,
                   state_json = CAST(@json AS jsonb),
                   updated_at_utc = now()
             WHERE cluster_id = @cluster AND version = @expected
         RETURNING version, state_json::text
            """, connection, tx);
        stateCommand.Parameters.AddWithValue("cluster", _clusterId);
        stateCommand.Parameters.AddWithValue("expected", expectedVersion);
        stateCommand.Parameters.AddWithValue("json", stateJson);

        await using var stateReader = await stateCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
        TransactionalStateSnapshot? committed = null;
        if (await stateReader.ReadAsync(ct).ConfigureAwait(false))
            committed = new TransactionalStateSnapshot(stateReader.GetInt64(0), stateReader.GetString(1));
        await stateReader.DisposeAsync().ConfigureAwait(false);

        if (committed is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        // Completion can update Command + RepositoryHealth + RecoveryRuns + Alarms and other resilience state.
        // Project the authoritative post-resilience document inside the SAME transaction.
        await ProjectNormalizedStateAsync(connection, tx, stateJson, ct).ConfigureAwait(false);

        await using (var completionFence = new NpgsqlCommand("""
            UPDATE yb_commands
               SET completion_lease_id=@lease
             WHERE cluster_id=@cluster AND command_id=@command AND agent_id=@agent
            """, connection, tx))
        {
            completionFence.Parameters.AddWithValue("lease", completionLeaseId);
            completionFence.Parameters.AddWithValue("cluster", _clusterId);
            completionFence.Parameters.AddWithValue("command", completedCommand.CommandId);
            completionFence.Parameters.AddWithValue("agent", completedCommand.AgentId);
            await completionFence.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return committed;
    }

    public async Task<TransactionalStateSnapshot?> TryCommitCommandMutationAsync(
        long expectedVersion,
        string stateJson,
        AgentCommand commandRecord,
        CancellationToken ct)
    {
        var entityJson = JsonSerializer.Serialize(commandRecord, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return await TryCommitFocusedMutationAsync(
            expectedVersion,
            stateJson,
            """
            INSERT INTO yb_commands(cluster_id, command_id, agent_id, command_type, created_at_utc, claimed_at_utc, lease_id, lease_expires_at_utc, last_lease_renewal_utc, completed_at_utc, succeeded, attempt_count, idempotency_key, error, payload)
            SELECT @cluster, (e->>'commandId')::uuid, (e->>'agentId')::uuid, e->>'type',
                   (e->>'createdAtUtc')::timestamptz, NULLIF(e->>'claimedAtUtc','')::timestamptz,
                   NULLIF(e->>'leaseId','')::uuid, NULLIF(e->>'leaseExpiresAtUtc','')::timestamptz, NULLIF(e->>'lastLeaseRenewalUtc','')::timestamptz,
                   NULLIF(e->>'completedAtUtc','')::timestamptz, COALESCE((e->>'succeeded')::boolean,false),
                   COALESCE((e->>'attemptCount')::integer,0), NULLIF(e->>'idempotencyKey',''), NULLIF(e->>'error',''), e
              FROM (SELECT CAST(@entity AS jsonb) e) x
            ON CONFLICT(cluster_id, command_id) DO UPDATE SET
              agent_id=excluded.agent_id, command_type=excluded.command_type, created_at_utc=excluded.created_at_utc,
              claimed_at_utc=excluded.claimed_at_utc, lease_id=excluded.lease_id, lease_expires_at_utc=excluded.lease_expires_at_utc,
              last_lease_renewal_utc=excluded.last_lease_renewal_utc, completed_at_utc=excluded.completed_at_utc,
              succeeded=excluded.succeeded, attempt_count=excluded.attempt_count, idempotency_key=excluded.idempotency_key,
              error=excluded.error, payload=excluded.payload
            """,
            entityJson,
            ct).ConfigureAwait(false);
    }

    public async Task<TransactionalStateSnapshot?> TryCommitPolicyMutationAsync(
        long expectedVersion,
        string stateJson,
        BackupPolicyRecord policy,
        CancellationToken ct)
    {
        var entityJson = JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return await TryCommitFocusedMutationAsync(
            expectedVersion,
            stateJson,
            """
            INSERT INTO yb_backup_policies(cluster_id, policy_id, agent_id, name, source_path, repository_root, repository_id, enabled, interval_minutes, next_run_at_utc, created_at_utc, payload)
            SELECT @cluster, (e->>'policyId')::uuid, (e->>'agentId')::uuid, e->>'name', e->>'sourcePath',
                   e->>'repositoryRoot', e->>'repositoryId', (e->>'enabled')::boolean, (e->>'intervalMinutes')::integer,
                   (e->>'nextRunAtUtc')::timestamptz, (e->>'createdAtUtc')::timestamptz, e
              FROM (SELECT CAST(@entity AS jsonb) e) x
            ON CONFLICT(cluster_id, policy_id) DO UPDATE SET
              agent_id=excluded.agent_id, name=excluded.name, source_path=excluded.source_path,
              repository_root=excluded.repository_root, repository_id=excluded.repository_id, enabled=excluded.enabled,
              interval_minutes=excluded.interval_minutes, next_run_at_utc=excluded.next_run_at_utc,
              created_at_utc=excluded.created_at_utc, payload=excluded.payload
            """,
            entityJson,
            ct).ConfigureAwait(false);
    }

    public async Task<TransactionalStateSnapshot?> TryCommitPolicyDeleteAsync(
        long expectedVersion,
        string stateJson,
        Guid policyId,
        CancellationToken ct)
    {
        return await TryCommitFocusedMutationAsync(
            expectedVersion,
            stateJson,
            "DELETE FROM yb_backup_policies WHERE cluster_id=@cluster AND policy_id=@entityId",
            null,
            ct,
            policyId).ConfigureAwait(false);
    }

    private async Task<TransactionalStateSnapshot?> TryCommitFocusedMutationAsync(
        long expectedVersion,
        string stateJson,
        string mutationSql,
        string? entityJson,
        CancellationToken ct,
        Guid? entityId = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

        await using var stateCommand = new NpgsqlCommand("""
            UPDATE yazmabackup_control_plane_state
               SET version = version + 1,
                   state_json = CAST(@json AS jsonb),
                   updated_at_utc = now()
             WHERE cluster_id = @cluster AND version = @expected
         RETURNING version, state_json::text
            """, connection, tx);
        stateCommand.Parameters.AddWithValue("cluster", _clusterId);
        stateCommand.Parameters.AddWithValue("expected", expectedVersion);
        stateCommand.Parameters.AddWithValue("json", stateJson);

        await using var reader = await stateCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
        TransactionalStateSnapshot? committed = null;
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            committed = new TransactionalStateSnapshot(reader.GetInt64(0), reader.GetString(1));
        await reader.DisposeAsync().ConfigureAwait(false);

        if (committed is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        await using var mutation = new NpgsqlCommand(mutationSql, connection, tx);
        mutation.Parameters.AddWithValue("cluster", _clusterId);
        if (entityJson is not null) mutation.Parameters.AddWithValue("entity", entityJson);
        if (entityId.HasValue) mutation.Parameters.AddWithValue("entityId", entityId.Value);
        await mutation.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return committed;
    }

    public async Task<TransactionalStateSnapshot> LoadAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT version, state_json::text FROM yazmabackup_control_plane_state WHERE cluster_id=@cluster", connection);
        command.Parameters.AddWithValue("cluster", _clusterId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("Transactional Control Plane state row is missing.");
        return new TransactionalStateSnapshot(reader.GetInt64(0), reader.GetString(1));
    }


    public async Task<IReadOnlyList<AgentRecord>> GetAgentsAsync(CancellationToken ct)
    {
        return await QueryPayloadsAsync<AgentRecord>(
            "SELECT payload::text FROM yb_agents WHERE cluster_id=@cluster ORDER BY machine_name", 0, ct).ConfigureAwait(false);
    }

    public async Task<AgentCommand?> GetCommandAsync(Guid commandId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM yb_commands WHERE cluster_id=@cluster AND command_id=@id", connection);
        command.Parameters.AddWithValue("cluster", _clusterId);
        command.Parameters.AddWithValue("id", commandId);
        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is string json ? DeserializePayload<AgentCommand>(json) : null;
    }

    public async Task<IReadOnlyList<AgentCommand>> GetRecentCommandsAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 500);
        return await QueryPayloadsAsync<AgentCommand>(
            "SELECT payload::text FROM yb_commands WHERE cluster_id=@cluster ORDER BY created_at_utc DESC LIMIT @limit", limit, ct).ConfigureAwait(false);
    }

    public async Task<OperationalCommandMetrics> GetOperationalCommandMetricsAsync(
        DateTimeOffset backupSinceUtc,
        DateTimeOffset restoreDrillSinceUtc,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT
              count(*) FILTER (WHERE command_type=@backup AND completed_at_utc>=@backupSince AND succeeded),
              count(*) FILTER (WHERE command_type=@backup AND completed_at_utc>=@backupSince AND NOT succeeded),
              count(*) FILTER (WHERE command_type=@drill AND completed_at_utc>=@drillSince AND succeeded),
              count(*) FILTER (WHERE command_type=@drill AND completed_at_utc>=@drillSince AND NOT succeeded)
            FROM yb_commands
            WHERE cluster_id=@cluster
            """, connection);
        command.Parameters.AddWithValue("cluster", _clusterId);
        command.Parameters.AddWithValue("backup", ((int)AgentCommandType.BackupPath).ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("drill", ((int)AgentCommandType.RestoreDrill).ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("backupSince", backupSinceUtc);
        command.Parameters.AddWithValue("drillSince", restoreDrillSinceUtc);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new OperationalCommandMetrics(0, 0, 0, 0);
        return new OperationalCommandMetrics(
            checked((int)reader.GetInt64(0)),
            checked((int)reader.GetInt64(1)),
            checked((int)reader.GetInt64(2)),
            checked((int)reader.GetInt64(3)));
    }

    public async Task<IReadOnlyList<BackupPolicyRecord>> GetBackupPoliciesAsync(CancellationToken ct)
    {
        return await QueryPayloadsAsync<BackupPolicyRecord>(
            "SELECT payload::text FROM yb_backup_policies WHERE cluster_id=@cluster ORDER BY name", 0, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEventRecord>> GetAuditEventsAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return await QueryPayloadsAsync<AuditEventRecord>(
            "SELECT payload::text FROM yb_audit_events WHERE cluster_id=@cluster ORDER BY occurred_at_utc DESC LIMIT @limit", limit, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlarmRecord>> GetAlarmsAsync(int limit, bool includeResolved, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var sql = includeResolved
            ? "SELECT payload::text FROM yb_alarms WHERE cluster_id=@cluster ORDER BY last_seen_at_utc DESC LIMIT @limit"
            : "SELECT payload::text FROM yb_alarms WHERE cluster_id=@cluster AND status<>@resolved ORDER BY last_seen_at_utc DESC LIMIT @limit";
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cluster", _clusterId);
        command.Parameters.AddWithValue("limit", limit);
        if (!includeResolved) command.Parameters.AddWithValue("resolved", "resolved");
        var rows = new List<AlarmRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(DeserializePayload<AlarmRecord>(reader.GetString(0)));
        return rows;
    }

    private async Task<IReadOnlyList<T>> QueryPayloadsAsync<T>(string sql, int limit, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cluster", _clusterId);
        if (limit > 0) command.Parameters.AddWithValue("limit", limit);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(DeserializePayload<T>(reader.GetString(0)));
        return rows;
    }

    private static T DeserializePayload<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException($"Normalized PostgreSQL payload could not be deserialized as {typeof(T).Name}.");

    public object GetStatus()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT version, updated_at_utc, pg_is_in_recovery(), current_database()
              FROM yazmabackup_control_plane_state
             WHERE cluster_id=@cluster
            """, connection);
        command.Parameters.AddWithValue("cluster", _clusterId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new { engine = "postgresql", clusterId = _clusterId, initialized = false };
        var version = reader.GetInt64(0);
        var updated = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
        var recovery = reader.GetBoolean(2);
        var database = reader.GetString(3);
        reader.Close();

        var counts = GetProjectionCounts(connection);
        return new
        {
            engine = "postgresql",
            clusterId = _clusterId,
            initialized = true,
            version,
            updatedAtUtc = updated,
            serverInRecovery = recovery,
            database,
            normalizedSchemaVersion = "13.0",
            normalizedProjection = counts,
            normalizedReadPath = "direct-sql",
            focusedMutationPath = "command-lease-engine+policies",
            commandLeaseEngine = "postgresql-skip-locked",
            completionTransaction = "multi-aggregate-atomic"
        };
    }

    private object GetProjectionCounts(NpgsqlConnection connection)
    {
        using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM yb_agents WHERE cluster_id=@cluster),
              (SELECT count(*) FROM yb_commands WHERE cluster_id=@cluster),
              (SELECT count(*) FROM yb_backup_policies WHERE cluster_id=@cluster),
              (SELECT count(*) FROM yb_audit_events WHERE cluster_id=@cluster),
              (SELECT count(*) FROM yb_alarms WHERE cluster_id=@cluster)
            """, connection);
        command.Parameters.AddWithValue("cluster", _clusterId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new { agents = 0L, commands = 0L, policies = 0L, auditEvents = 0L, alarms = 0L };
        return new
        {
            agents = reader.GetInt64(0),
            commands = reader.GetInt64(1),
            policies = reader.GetInt64(2),
            auditEvents = reader.GetInt64(3),
            alarms = reader.GetInt64(4)
        };
    }

    private void SyncNormalizedProjection(NpgsqlConnection connection, NpgsqlTransaction tx, string stateJson)
    {
        using var command = BuildProjectionCommand(connection, tx, stateJson);
        command.ExecuteNonQuery();
    }

    private async Task SyncNormalizedProjectionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string stateJson, CancellationToken ct)
    {
        await using var command = BuildProjectionCommand(connection, tx, stateJson);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private NpgsqlCommand BuildProjectionCommand(NpgsqlConnection connection, NpgsqlTransaction tx, string stateJson)
    {
        var command = new NpgsqlCommand(NormalizedProjectionSql, connection, tx);
        command.Parameters.AddWithValue("cluster", _clusterId);
        command.Parameters.AddWithValue("state", stateJson);
        return command;
    }

    private void EnsureSchema()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS yazmabackup_control_plane_state(
                cluster_id text PRIMARY KEY,
                version bigint NOT NULL CHECK(version >= 0),
                state_json jsonb NOT NULL,
                updated_at_utc timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_yb_cp_state_updated
                ON yazmabackup_control_plane_state(updated_at_utc);

            CREATE TABLE IF NOT EXISTS yb_schema_migrations(
                version text PRIMARY KEY,
                description text NOT NULL,
                applied_at_utc timestamptz NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS yb_agents(
                cluster_id text NOT NULL,
                agent_id uuid NOT NULL,
                machine_name text NOT NULL,
                operating_system text NOT NULL,
                agent_version text NULL,
                enrolled_at_utc timestamptz NOT NULL,
                last_seen_utc timestamptz NOT NULL,
                protection_status text NOT NULL,
                assigned_user text NULL,
                payload jsonb NOT NULL,
                PRIMARY KEY(cluster_id, agent_id)
            );

            CREATE TABLE IF NOT EXISTS yb_commands(
                cluster_id text NOT NULL,
                command_id uuid NOT NULL,
                agent_id uuid NOT NULL,
                command_type text NOT NULL,
                created_at_utc timestamptz NOT NULL,
                claimed_at_utc timestamptz NULL,
                lease_id uuid NULL,
                lease_expires_at_utc timestamptz NULL,
                last_lease_renewal_utc timestamptz NULL,
                completed_at_utc timestamptz NULL,
                succeeded boolean NOT NULL,
                attempt_count integer NOT NULL,
                idempotency_key text NULL,
                error text NULL,
                payload jsonb NOT NULL,
                PRIMARY KEY(cluster_id, command_id),
                CONSTRAINT fk_yb_commands_agent FOREIGN KEY(cluster_id, agent_id)
                    REFERENCES yb_agents(cluster_id, agent_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS yb_backup_policies(
                cluster_id text NOT NULL,
                policy_id uuid NOT NULL,
                agent_id uuid NOT NULL,
                name text NOT NULL,
                source_path text NOT NULL,
                repository_root text NOT NULL,
                repository_id text NOT NULL,
                enabled boolean NOT NULL,
                interval_minutes integer NOT NULL,
                next_run_at_utc timestamptz NOT NULL,
                created_at_utc timestamptz NOT NULL,
                payload jsonb NOT NULL,
                PRIMARY KEY(cluster_id, policy_id),
                CONSTRAINT fk_yb_policies_agent FOREIGN KEY(cluster_id, agent_id)
                    REFERENCES yb_agents(cluster_id, agent_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS yb_audit_events(
                cluster_id text NOT NULL,
                event_id uuid NOT NULL,
                occurred_at_utc timestamptz NOT NULL,
                actor text NOT NULL,
                action text NOT NULL,
                method text NOT NULL,
                path text NOT NULL,
                status_code integer NOT NULL,
                correlation_id text NOT NULL,
                payload jsonb NOT NULL,
                PRIMARY KEY(cluster_id, event_id)
            );

            CREATE TABLE IF NOT EXISTS yb_alarms(
                cluster_id text NOT NULL,
                alarm_id uuid NOT NULL,
                fingerprint text NOT NULL,
                severity text NOT NULL,
                category text NOT NULL,
                title text NOT NULL,
                status text NOT NULL,
                agent_id uuid NULL,
                first_seen_at_utc timestamptz NOT NULL,
                last_seen_at_utc timestamptz NOT NULL,
                due_at_utc timestamptz NULL,
                payload jsonb NOT NULL,
                PRIMARY KEY(cluster_id, alarm_id)
            );

            ALTER TABLE yb_commands ADD COLUMN IF NOT EXISTS lease_id uuid NULL;
            ALTER TABLE yb_commands ADD COLUMN IF NOT EXISTS lease_expires_at_utc timestamptz NULL;
            ALTER TABLE yb_commands ADD COLUMN IF NOT EXISTS last_lease_renewal_utc timestamptz NULL;
            ALTER TABLE yb_commands ADD COLUMN IF NOT EXISTS completion_lease_id uuid NULL;

            CREATE INDEX IF NOT EXISTS ix_yb_commands_claimable
                ON yb_commands(cluster_id, agent_id, created_at_utc)
                WHERE completed_at_utc IS NULL;
            CREATE INDEX IF NOT EXISTS ix_yb_commands_lease_expiry
                ON yb_commands(cluster_id, lease_expires_at_utc)
                WHERE completed_at_utc IS NULL;

            CREATE INDEX IF NOT EXISTS ix_yb_agents_last_seen ON yb_agents(cluster_id, last_seen_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_yb_agents_machine_name ON yb_agents(cluster_id, machine_name);
            CREATE INDEX IF NOT EXISTS ix_yb_commands_agent_created ON yb_commands(cluster_id, agent_id, created_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_yb_commands_pending ON yb_commands(cluster_id, created_at_utc) WHERE completed_at_utc IS NULL;
            CREATE INDEX IF NOT EXISTS ix_yb_commands_type_completed ON yb_commands(cluster_id, command_type, completed_at_utc DESC);
            DROP INDEX IF EXISTS ux_yb_commands_idempotency;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_yb_commands_idempotency_v2
                ON yb_commands(cluster_id, agent_id, command_type, idempotency_key)
                WHERE idempotency_key IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_yb_policies_due ON yb_backup_policies(cluster_id, enabled, next_run_at_utc);
            CREATE INDEX IF NOT EXISTS ix_yb_policies_agent ON yb_backup_policies(cluster_id, agent_id);
            CREATE INDEX IF NOT EXISTS ix_yb_audit_time ON yb_audit_events(cluster_id, occurred_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_yb_audit_actor ON yb_audit_events(cluster_id, actor, occurred_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_yb_audit_correlation ON yb_audit_events(cluster_id, correlation_id);
            CREATE INDEX IF NOT EXISTS ix_yb_audit_brin_time ON yb_audit_events USING brin(occurred_at_utc);
            CREATE INDEX IF NOT EXISTS ix_yb_alarms_status ON yb_alarms(cluster_id, status, severity, last_seen_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_yb_alarms_agent ON yb_alarms(cluster_id, agent_id, last_seen_at_utc DESC);

            INSERT INTO yb_schema_migrations(version, description)
            VALUES ('13.0', 'Normalized hot aggregates: agents, commands, backup policies, audit events, alarms')
            ON CONFLICT (version) DO NOTHING;
            """, connection);
        command.ExecuteNonQuery();
    }

    private const string NormalizedProjectionSql = """
        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        INSERT INTO yb_agents(cluster_id, agent_id, machine_name, operating_system, agent_version, enrolled_at_utc, last_seen_utc, protection_status, assigned_user, payload)
        SELECT @cluster, e.key::uuid, e.value->>'machineName', e.value->>'operatingSystem', NULLIF(e.value->>'agentVersion',''),
               (e.value->>'enrolledAtUtc')::timestamptz, (e.value->>'lastSeenUtc')::timestamptz,
               COALESCE(NULLIF(e.value->>'protectionStatus',''),'healthy'), NULLIF(e.value->>'assignedUser',''), e.value
          FROM input, LATERAL jsonb_each(COALESCE(doc->'agents','{}'::jsonb)) e
        ON CONFLICT(cluster_id, agent_id) DO UPDATE SET
          machine_name=excluded.machine_name, operating_system=excluded.operating_system, agent_version=excluded.agent_version,
          enrolled_at_utc=excluded.enrolled_at_utc, last_seen_utc=excluded.last_seen_utc, protection_status=excluded.protection_status,
          assigned_user=excluded.assigned_user, payload=excluded.payload;

        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        INSERT INTO yb_commands(cluster_id, command_id, agent_id, command_type, created_at_utc, claimed_at_utc, lease_id, lease_expires_at_utc, last_lease_renewal_utc, completed_at_utc, succeeded, attempt_count, idempotency_key, error, payload)
        SELECT @cluster, e.key::uuid, (e.value->>'agentId')::uuid, COALESCE(e.value->>'type','unknown'),
               (e.value->>'createdAtUtc')::timestamptz, NULLIF(e.value->>'claimedAtUtc','')::timestamptz,
               NULLIF(e.value->>'leaseId','')::uuid, NULLIF(e.value->>'leaseExpiresAtUtc','')::timestamptz, NULLIF(e.value->>'lastLeaseRenewalUtc','')::timestamptz,
               NULLIF(e.value->>'completedAtUtc','')::timestamptz, COALESCE((e.value->>'succeeded')::boolean,false),
               COALESCE((e.value->>'attemptCount')::integer,0), NULLIF(e.value->>'idempotencyKey',''), NULLIF(e.value->>'error',''), e.value
          FROM input, LATERAL jsonb_each(COALESCE(doc->'commands','{}'::jsonb)) e
        ON CONFLICT(cluster_id, command_id) DO UPDATE SET
          agent_id=excluded.agent_id, command_type=excluded.command_type, created_at_utc=excluded.created_at_utc,
          claimed_at_utc=excluded.claimed_at_utc, lease_id=excluded.lease_id, lease_expires_at_utc=excluded.lease_expires_at_utc,
          last_lease_renewal_utc=excluded.last_lease_renewal_utc, completed_at_utc=excluded.completed_at_utc, succeeded=excluded.succeeded,
          attempt_count=excluded.attempt_count, idempotency_key=excluded.idempotency_key, error=excluded.error, payload=excluded.payload;

        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        INSERT INTO yb_backup_policies(cluster_id, policy_id, agent_id, name, source_path, repository_root, repository_id, enabled, interval_minutes, next_run_at_utc, created_at_utc, payload)
        SELECT @cluster, e.key::uuid, (e.value->>'agentId')::uuid, e.value->>'name', e.value->>'sourcePath',
               e.value->>'repositoryRoot', e.value->>'repositoryId', (e.value->>'enabled')::boolean,
               (e.value->>'intervalMinutes')::integer, (e.value->>'nextRunAtUtc')::timestamptz,
               (e.value->>'createdAtUtc')::timestamptz, e.value
          FROM input, LATERAL jsonb_each(COALESCE(doc->'backupPolicies','{}'::jsonb)) e
        ON CONFLICT(cluster_id, policy_id) DO UPDATE SET
          agent_id=excluded.agent_id, name=excluded.name, source_path=excluded.source_path, repository_root=excluded.repository_root,
          repository_id=excluded.repository_id, enabled=excluded.enabled, interval_minutes=excluded.interval_minutes,
          next_run_at_utc=excluded.next_run_at_utc, created_at_utc=excluded.created_at_utc, payload=excluded.payload;

        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        INSERT INTO yb_audit_events(cluster_id, event_id, occurred_at_utc, actor, action, method, path, status_code, correlation_id, payload)
        SELECT @cluster, e.key::uuid, (e.value->>'occurredAtUtc')::timestamptz, e.value->>'actor', e.value->>'action',
               e.value->>'method', e.value->>'path', (e.value->>'statusCode')::integer, e.value->>'correlationId', e.value
          FROM input, LATERAL jsonb_each(COALESCE(doc->'auditEvents','{}'::jsonb)) e
        ON CONFLICT(cluster_id, event_id) DO UPDATE SET
          occurred_at_utc=excluded.occurred_at_utc, actor=excluded.actor, action=excluded.action, method=excluded.method,
          path=excluded.path, status_code=excluded.status_code, correlation_id=excluded.correlation_id, payload=excluded.payload;

        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        INSERT INTO yb_alarms(cluster_id, alarm_id, fingerprint, severity, category, title, status, agent_id, first_seen_at_utc, last_seen_at_utc, due_at_utc, payload)
        SELECT @cluster, e.key::uuid, e.value->>'fingerprint', e.value->>'severity', e.value->>'category', e.value->>'title',
               e.value->>'status', NULLIF(e.value->>'agentId','')::uuid, (e.value->>'firstSeenAtUtc')::timestamptz,
               (e.value->>'lastSeenAtUtc')::timestamptz, NULLIF(e.value->>'dueAtUtc','')::timestamptz, e.value
          FROM input, LATERAL jsonb_each(COALESCE(doc->'alarms','{}'::jsonb)) e
        ON CONFLICT(cluster_id, alarm_id) DO UPDATE SET
          fingerprint=excluded.fingerprint, severity=excluded.severity, category=excluded.category, title=excluded.title,
          status=excluded.status, agent_id=excluded.agent_id, first_seen_at_utc=excluded.first_seen_at_utc,
          last_seen_at_utc=excluded.last_seen_at_utc, due_at_utc=excluded.due_at_utc, payload=excluded.payload;

        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        DELETE FROM yb_commands c
         WHERE c.cluster_id=@cluster
           AND NOT EXISTS (SELECT 1 FROM input, LATERAL jsonb_object_keys(COALESCE(input.doc->'commands','{}'::jsonb)) k WHERE k::uuid=c.command_id);

        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        DELETE FROM yb_backup_policies p
         WHERE p.cluster_id=@cluster
           AND NOT EXISTS (SELECT 1 FROM input, LATERAL jsonb_object_keys(COALESCE(input.doc->'backupPolicies','{}'::jsonb)) k WHERE k::uuid=p.policy_id);

        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        DELETE FROM yb_audit_events a
         WHERE a.cluster_id=@cluster
           AND NOT EXISTS (SELECT 1 FROM input, LATERAL jsonb_object_keys(COALESCE(input.doc->'auditEvents','{}'::jsonb)) k WHERE k::uuid=a.event_id);

        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        DELETE FROM yb_alarms a
         WHERE a.cluster_id=@cluster
           AND NOT EXISTS (SELECT 1 FROM input, LATERAL jsonb_object_keys(COALESCE(input.doc->'alarms','{}'::jsonb)) k WHERE k::uuid=a.alarm_id);

        WITH input AS (SELECT CAST(@state AS jsonb) AS doc)
        DELETE FROM yb_agents a
         WHERE a.cluster_id=@cluster
           AND NOT EXISTS (SELECT 1 FROM input, LATERAL jsonb_object_keys(COALESCE(input.doc->'agents','{}'::jsonb)) k WHERE k::uuid=a.agent_id);
        """;
}
