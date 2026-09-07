BEGIN;

CREATE TABLE IF NOT EXISTS recovery_runbooks (
    runbook_id uuid PRIMARY KEY,
    name text NOT NULL,
    recovery_plan_ids jsonb NOT NULL,
    rto_budget_minutes integer NOT NULL CHECK (rto_budget_minutes BETWEEN 1 AND 10080),
    interval_days integer NOT NULL CHECK (interval_days BETWEEN 1 AND 365),
    enabled boolean NOT NULL,
    created_at_utc timestamptz NOT NULL,
    last_run_at_utc timestamptz NULL,
    next_run_at_utc timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_recovery_runbooks_due ON recovery_runbooks(enabled, next_run_at_utc);

CREATE TABLE IF NOT EXISTS recovery_runbook_runs (
    run_id uuid PRIMARY KEY,
    runbook_id uuid NOT NULL REFERENCES recovery_runbooks(runbook_id),
    started_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz NULL,
    status text NOT NULL,
    rto_budget_minutes integer NOT NULL,
    current_step_index integer NOT NULL,
    steps_json jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_recovery_runbook_runs_runbook_started ON recovery_runbook_runs(runbook_id, started_at_utc DESC);

CREATE TABLE IF NOT EXISTS integration_credentials (
    credential_id uuid PRIMARY KEY,
    name text NOT NULL,
    purpose text NOT NULL,
    token_hash text NOT NULL UNIQUE,
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    last_used_at_utc timestamptz NULL,
    revoked boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_integration_credentials_active ON integration_credentials(purpose, expires_at_utc) WHERE revoked = false;

CREATE TABLE IF NOT EXISTS meshcentral_sync_events (
    event_id uuid PRIMARY KEY,
    agent_id uuid NOT NULL,
    node_id text NOT NULL,
    node_status text NOT NULL,
    deployment_status text NULL,
    reported_at_utc timestamptz NOT NULL,
    received_at_utc timestamptz NOT NULL,
    credential_id uuid NOT NULL REFERENCES integration_credentials(credential_id)
);
CREATE INDEX IF NOT EXISTS ix_meshcentral_sync_events_agent_received ON meshcentral_sync_events(agent_id, received_at_utc DESC);

CREATE TABLE IF NOT EXISTS repository_circuit_observations (
    observation_id uuid PRIMARY KEY,
    agent_id uuid NOT NULL,
    repository_id text NOT NULL,
    consecutive_failures integer NOT NULL,
    last_failure_at_utc timestamptz NULL,
    open_until_utc timestamptz NULL,
    last_error text NULL,
    observed_at_utc timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_repository_circuit_observations_agent_repo ON repository_circuit_observations(agent_id, repository_id, observed_at_utc DESC);

CREATE TABLE IF NOT EXISTS recovery_evidence_exports (
    evidence_id uuid PRIMARY KEY,
    runbook_run_id uuid NOT NULL REFERENCES recovery_runbook_runs(run_id),
    generated_at_utc timestamptz NOT NULL,
    payload_sha256 text NOT NULL,
    signature_algorithm text NOT NULL,
    signature_base64 text NOT NULL,
    public_key_fingerprint_sha256 text NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_recovery_evidence_exports_run ON recovery_evidence_exports(runbook_run_id, generated_at_utc DESC);

COMMIT;
