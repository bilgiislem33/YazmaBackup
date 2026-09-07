BEGIN;

CREATE TABLE IF NOT EXISTS schema_migrations (
    version bigint PRIMARY KEY,
    applied_at_utc timestamptz NOT NULL DEFAULT now(),
    checksum_sha256 char(64) NOT NULL
);

CREATE TABLE IF NOT EXISTS agents (
    agent_id uuid PRIMARY KEY,
    machine_name varchar(128) NOT NULL,
    operating_system varchar(256) NOT NULL,
    token_hash char(64) NOT NULL,
    enrolled_at_utc timestamptz NOT NULL,
    last_seen_utc timestamptz NOT NULL,
    agent_version varchar(64),
    capabilities jsonb NOT NULL DEFAULT '[]'::jsonb,
    key_exchange_public_key_pem text,
    CONSTRAINT ck_agents_machine_name CHECK (char_length(machine_name) BETWEEN 1 AND 128)
);
CREATE INDEX IF NOT EXISTS ix_agents_last_seen ON agents(last_seen_utc DESC);

CREATE TABLE IF NOT EXISTS enrollment_grants (
    grant_id uuid PRIMARY KEY,
    token_hash char(64) NOT NULL UNIQUE,
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    remaining_uses integer NOT NULL CHECK (remaining_uses BETWEEN 1 AND 5000)
);
CREATE INDEX IF NOT EXISTS ix_enrollment_grants_expiry ON enrollment_grants(expires_at_utc);

CREATE TABLE IF NOT EXISTS agent_commands (
    command_id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agents(agent_id) ON DELETE CASCADE,
    command_type integer NOT NULL,
    payload_json jsonb NOT NULL,
    created_at_utc timestamptz NOT NULL,
    claimed_at_utc timestamptz,
    completed_at_utc timestamptz,
    result_json jsonb,
    succeeded boolean NOT NULL DEFAULT false,
    error text,
    lease_id uuid,
    lease_expires_at_utc timestamptz,
    last_lease_renewal_utc timestamptz,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    idempotency_key varchar(128)
);
CREATE INDEX IF NOT EXISTS ix_agent_commands_pending ON agent_commands(agent_id, created_at_utc) WHERE completed_at_utc IS NULL;
CREATE INDEX IF NOT EXISTS ix_agent_commands_completed ON agent_commands(completed_at_utc DESC) WHERE completed_at_utc IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_commands_idempotency ON agent_commands(agent_id, command_type, idempotency_key) WHERE idempotency_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS backup_policies (
    policy_id uuid PRIMARY KEY,
    name varchar(128) NOT NULL,
    agent_id uuid NOT NULL REFERENCES agents(agent_id) ON DELETE CASCADE,
    source_path text NOT NULL,
    repository_root text NOT NULL,
    repository_id varchar(128) NOT NULL,
    require_snapshot boolean NOT NULL DEFAULT true,
    interval_minutes integer NOT NULL CHECK (interval_minutes BETWEEN 5 AND 43200),
    active_bytes_per_second bigint NOT NULL CHECK (active_bytes_per_second BETWEEN 0 AND 1073741824),
    idle_bytes_per_second bigint NOT NULL CHECK (idle_bytes_per_second BETWEEN 0 AND 1073741824),
    user_idle_threshold_seconds integer NOT NULL CHECK (user_idle_threshold_seconds BETWEEN 30 AND 86400),
    retention_json jsonb NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    created_at_utc timestamptz NOT NULL,
    last_scheduled_at_utc timestamptz,
    next_run_at_utc timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_backup_policies_due ON backup_policies(enabled, next_run_at_utc) WHERE enabled = true;

CREATE TABLE IF NOT EXISTS management_users (
    user_id uuid PRIMARY KEY,
    username varchar(64) NOT NULL,
    display_name varchar(128) NOT NULL,
    password_hash_base64 varchar(128) NOT NULL,
    password_salt_base64 varchar(64) NOT NULL,
    password_iterations integer NOT NULL CHECK (password_iterations >= 100000),
    roles jsonb NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    must_change_password boolean NOT NULL DEFAULT true,
    created_at_utc timestamptz NOT NULL,
    last_login_at_utc timestamptz,
    failed_login_count integer NOT NULL DEFAULT 0 CHECK (failed_login_count >= 0),
    locked_until_utc timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_management_users_username_ci ON management_users(lower(username));

CREATE TABLE IF NOT EXISTS audit_events (
    event_id uuid PRIMARY KEY,
    occurred_at_utc timestamptz NOT NULL,
    actor varchar(128) NOT NULL,
    action varchar(256) NOT NULL,
    method varchar(16) NOT NULL,
    path text NOT NULL,
    status_code integer NOT NULL CHECK (status_code BETWEEN 100 AND 599),
    remote_address varchar(128),
    correlation_id varchar(256) NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_audit_events_occurred ON audit_events(occurred_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_audit_events_actor ON audit_events(actor, occurred_at_utc DESC);

COMMIT;
