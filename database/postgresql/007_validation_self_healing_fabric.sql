BEGIN;

CREATE TABLE IF NOT EXISTS validation_runs (
    validation_run_id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agents(agent_id) ON DELETE CASCADE,
    source_path text NOT NULL,
    repository_id text NOT NULL,
    repository_root text NOT NULL,
    started_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz,
    score integer,
    grade text,
    result_json jsonb,
    CONSTRAINT ck_validation_score CHECK (score IS NULL OR (score >= 0 AND score <= 100))
);
CREATE INDEX IF NOT EXISTS ix_validation_runs_agent_started ON validation_runs(agent_id, started_at_utc DESC);

CREATE TABLE IF NOT EXISTS self_healing_events (
    event_id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agents(agent_id) ON DELETE CASCADE,
    repository_id text NOT NULL,
    action_id text NOT NULL,
    requested_by text,
    requested_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz,
    succeeded boolean,
    result_json jsonb
);
CREATE INDEX IF NOT EXISTS ix_self_healing_events_agent_time ON self_healing_events(agent_id, requested_at_utc DESC);

CREATE TABLE IF NOT EXISTS restore_sandbox_runs (
    sandbox_run_id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agents(agent_id) ON DELETE CASCADE,
    backup_id text NOT NULL,
    repository_id text NOT NULL,
    sandbox_root text NOT NULL,
    started_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz,
    verified_files integer,
    verified_bytes bigint,
    status text NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_restore_sandbox_agent_time ON restore_sandbox_runs(agent_id, started_at_utc DESC);

COMMIT;
