BEGIN;

CREATE TABLE IF NOT EXISTS repository_health_records (
    health_id uuid PRIMARY KEY,
    policy_id uuid NOT NULL REFERENCES backup_policies(policy_id) ON DELETE CASCADE,
    agent_id uuid NOT NULL REFERENCES agents(agent_id) ON DELETE CASCADE,
    repository_id varchar(128) NOT NULL,
    repository_root text NOT NULL,
    measured_at_utc timestamptz NOT NULL,
    total_bytes bigint NOT NULL CHECK (total_bytes >= 0),
    free_bytes bigint NOT NULL CHECK (free_bytes >= 0),
    repository_physical_bytes bigint NOT NULL CHECK (repository_physical_bytes >= 0),
    restore_point_count integer NOT NULL CHECK (restore_point_count >= 0),
    latest_restore_point_utc timestamptz,
    new_bytes_last_7_days bigint NOT NULL CHECK (new_bytes_last_7_days >= 0),
    daily_growth_bytes double precision NOT NULL CHECK (daily_growth_bytes >= 0),
    estimated_days_to_full double precision,
    status varchar(16) NOT NULL CHECK (status IN ('healthy','warning','critical')),
    reason varchar(512)
);
CREATE INDEX IF NOT EXISTS ix_repository_health_policy_time ON repository_health_records(policy_id, measured_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_repository_health_status_time ON repository_health_records(status, measured_at_utc DESC);

CREATE TABLE IF NOT EXISTS recovery_plans (
    plan_id uuid PRIMARY KEY,
    name varchar(128) NOT NULL,
    policy_ids jsonb NOT NULL,
    max_parallel_agents integer NOT NULL CHECK (max_parallel_agents BETWEEN 1 AND 50),
    rto_target_minutes integer NOT NULL CHECK (rto_target_minutes BETWEEN 1 AND 10080),
    interval_days integer NOT NULL CHECK (interval_days BETWEEN 1 AND 365),
    enabled boolean NOT NULL DEFAULT true,
    created_at_utc timestamptz NOT NULL,
    last_run_at_utc timestamptz,
    next_run_at_utc timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_recovery_plans_due ON recovery_plans(enabled, next_run_at_utc) WHERE enabled = true;

CREATE TABLE IF NOT EXISTS recovery_runs (
    run_id uuid PRIMARY KEY,
    plan_id uuid NOT NULL REFERENCES recovery_plans(plan_id) ON DELETE CASCADE,
    started_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz,
    status varchar(16) NOT NULL CHECK (status IN ('running','succeeded','failed')),
    rto_target_minutes integer NOT NULL CHECK (rto_target_minutes > 0),
    verified_bytes bigint NOT NULL DEFAULT 0 CHECK (verified_bytes >= 0),
    longest_restore_milliseconds bigint NOT NULL DEFAULT 0 CHECK (longest_restore_milliseconds >= 0)
);
CREATE INDEX IF NOT EXISTS ix_recovery_runs_plan_time ON recovery_runs(plan_id, started_at_utc DESC);

CREATE TABLE IF NOT EXISTS recovery_run_targets (
    target_id uuid PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES recovery_runs(run_id) ON DELETE CASCADE,
    policy_id uuid NOT NULL,
    agent_id uuid,
    command_id uuid,
    status varchar(16) NOT NULL CHECK (status IN ('pending','running','succeeded','failed')),
    succeeded boolean,
    verified_bytes bigint NOT NULL DEFAULT 0 CHECK (verified_bytes >= 0),
    duration_milliseconds bigint NOT NULL DEFAULT 0 CHECK (duration_milliseconds >= 0),
    error varchar(512)
);
CREATE INDEX IF NOT EXISTS ix_recovery_run_targets_run ON recovery_run_targets(run_id, status);

CREATE TABLE IF NOT EXISTS meshcentral_links (
    agent_id uuid PRIMARY KEY REFERENCES agents(agent_id) ON DELETE CASCADE,
    meshcentral_base_uri text NOT NULL,
    node_id varchar(512) NOT NULL,
    linked_at_utc timestamptz NOT NULL,
    last_synchronized_at_utc timestamptz,
    last_known_node_status varchar(64),
    last_deployment_status varchar(128)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_meshcentral_links_node ON meshcentral_links(meshcentral_base_uri, node_id);

CREATE TABLE IF NOT EXISTS notification_routes (
    route_id uuid PRIMARY KEY,
    name varchar(128) NOT NULL,
    destination text NOT NULL,
    protected_hmac_secret text NOT NULL,
    severities jsonb NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    created_at_utc timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS notification_deliveries (
    delivery_id uuid PRIMARY KEY,
    route_id uuid NOT NULL REFERENCES notification_routes(route_id) ON DELETE CASCADE,
    alarm_id uuid NOT NULL REFERENCES alarms(alarm_id) ON DELETE CASCADE,
    alarm_version_utc timestamptz NOT NULL,
    created_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count BETWEEN 0 AND 3),
    succeeded boolean NOT NULL DEFAULT false,
    error varchar(512)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_delivery_version ON notification_deliveries(route_id, alarm_id, alarm_version_utc);
CREATE INDEX IF NOT EXISTS ix_notification_delivery_pending ON notification_deliveries(created_at_utc) WHERE completed_at_utc IS NULL;

CREATE OR REPLACE FUNCTION yazmabackup_try_acquire_cluster_lease(
    p_lease_name varchar,
    p_owner_id varchar,
    p_lease_id uuid,
    p_now timestamptz,
    p_expires timestamptz
) RETURNS TABLE(lease_name varchar, owner_id varchar, lease_id uuid, epoch bigint, acquired_at_utc timestamptz, expires_at_utc timestamptz)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO cluster_leases(lease_name, owner_id, lease_id, epoch, acquired_at_utc, expires_at_utc)
    VALUES (p_lease_name, p_owner_id, p_lease_id, 1, p_now, p_expires)
    ON CONFLICT (lease_name) DO UPDATE
    SET owner_id = CASE WHEN cluster_leases.expires_at_utc <= p_now OR cluster_leases.owner_id = p_owner_id THEN p_owner_id ELSE cluster_leases.owner_id END,
        lease_id = CASE WHEN cluster_leases.expires_at_utc <= p_now AND cluster_leases.owner_id <> p_owner_id THEN p_lease_id ELSE cluster_leases.lease_id END,
        epoch = CASE WHEN cluster_leases.expires_at_utc <= p_now AND cluster_leases.owner_id <> p_owner_id THEN cluster_leases.epoch + 1 ELSE cluster_leases.epoch END,
        acquired_at_utc = CASE WHEN cluster_leases.expires_at_utc <= p_now AND cluster_leases.owner_id <> p_owner_id THEN p_now ELSE cluster_leases.acquired_at_utc END,
        expires_at_utc = CASE WHEN cluster_leases.expires_at_utc <= p_now OR cluster_leases.owner_id = p_owner_id THEN p_expires ELSE cluster_leases.expires_at_utc END
    WHERE cluster_leases.expires_at_utc <= p_now OR cluster_leases.owner_id = p_owner_id;

    RETURN QUERY
    SELECT c.lease_name, c.owner_id, c.lease_id, c.epoch, c.acquired_at_utc, c.expires_at_utc
    FROM cluster_leases c
    WHERE c.lease_name = p_lease_name AND c.owner_id = p_owner_id AND c.expires_at_utc > p_now;
END;
$$;

COMMIT;
