BEGIN;

CREATE TABLE IF NOT EXISTS management_api_tokens (
    token_id uuid PRIMARY KEY,
    name varchar(64) NOT NULL,
    token_hash char(64) NOT NULL UNIQUE,
    roles jsonb NOT NULL,
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    last_used_at_utc timestamptz,
    revoked boolean NOT NULL DEFAULT false,
    CONSTRAINT ck_management_api_tokens_expiry CHECK (expires_at_utc > created_at_utc)
);
CREATE INDEX IF NOT EXISTS ix_management_api_tokens_active
    ON management_api_tokens(expires_at_utc)
    WHERE revoked = false;

CREATE TABLE IF NOT EXISTS break_glass_recovery_codes (
    code_id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES management_users(user_id) ON DELETE CASCADE,
    code_hash char(64) NOT NULL UNIQUE,
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    used_at_utc timestamptz,
    CONSTRAINT ck_break_glass_expiry CHECK (expires_at_utc > created_at_utc)
);
CREATE INDEX IF NOT EXISTS ix_break_glass_user_active
    ON break_glass_recovery_codes(user_id, expires_at_utc)
    WHERE used_at_utc IS NULL;

CREATE TABLE IF NOT EXISTS external_identities (
    identity_id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES management_users(user_id) ON DELETE CASCADE,
    issuer varchar(512) NOT NULL,
    subject varchar(256) NOT NULL,
    linked_at_utc timestamptz NOT NULL,
    last_login_at_utc timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_external_identities_issuer_subject
    ON external_identities(issuer, subject);
CREATE INDEX IF NOT EXISTS ix_external_identities_user ON external_identities(user_id);

CREATE TABLE IF NOT EXISTS alarms (
    alarm_id uuid PRIMARY KEY,
    fingerprint varchar(256) NOT NULL UNIQUE,
    severity varchar(16) NOT NULL CHECK (severity IN ('info','warning','critical')),
    category varchar(64) NOT NULL,
    title varchar(160) NOT NULL,
    details varchar(1024),
    status varchar(24) NOT NULL CHECK (status IN ('open','acknowledged','resolved')),
    agent_id uuid REFERENCES agents(agent_id) ON DELETE SET NULL,
    first_seen_at_utc timestamptz NOT NULL,
    last_seen_at_utc timestamptz NOT NULL,
    acknowledged_at_utc timestamptz,
    acknowledged_by varchar(128),
    resolved_at_utc timestamptz
);
CREATE INDEX IF NOT EXISTS ix_alarms_active ON alarms(severity, last_seen_at_utc DESC)
    WHERE status <> 'resolved';
CREATE INDEX IF NOT EXISTS ix_alarms_agent ON alarms(agent_id, last_seen_at_utc DESC)
    WHERE agent_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS cluster_leases (
    lease_name varchar(128) PRIMARY KEY,
    owner_id varchar(128) NOT NULL,
    lease_id uuid NOT NULL,
    epoch bigint NOT NULL CHECK (epoch > 0),
    acquired_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_cluster_lease_expiry CHECK (expires_at_utc > acquired_at_utc)
);
CREATE INDEX IF NOT EXISTS ix_cluster_leases_expiry ON cluster_leases(expires_at_utc);

CREATE TABLE IF NOT EXISTS control_plane_state_snapshots (
    snapshot_id uuid PRIMARY KEY,
    source_schema_version integer NOT NULL CHECK (source_schema_version > 0),
    source_sha256 char(64) NOT NULL,
    source_json jsonb NOT NULL,
    imported_at_utc timestamptz NOT NULL DEFAULT now(),
    verified_at_utc timestamptz,
    cutover_status varchar(32) NOT NULL DEFAULT 'staged' CHECK (cutover_status IN ('staged','verified','activated','rolled-back'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_control_plane_state_snapshots_hash
    ON control_plane_state_snapshots(source_sha256);

COMMIT;
