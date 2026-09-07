BEGIN;

ALTER TABLE agents
    ADD COLUMN IF NOT EXISTS protection_status varchar(32) NOT NULL DEFAULT 'healthy',
    ADD COLUMN IF NOT EXISTS protection_reason varchar(256),
    ADD COLUMN IF NOT EXISTS protection_triggered_at_utc timestamptz,
    ADD COLUMN IF NOT EXISTS protection_incident_id uuid;

ALTER TABLE backup_policies
    ADD COLUMN IF NOT EXISTS protection_policy_json jsonb NOT NULL DEFAULT '{"enabled":true}'::jsonb;

CREATE TABLE IF NOT EXISTS protection_incidents (
    incident_id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agents(agent_id) ON DELETE CASCADE,
    triggered_at_utc timestamptz NOT NULL,
    reason varchar(256) NOT NULL,
    assessment_json jsonb NOT NULL,
    status varchar(32) NOT NULL CHECK (status IN ('open','cleared')),
    cleared_at_utc timestamptz,
    cleared_by varchar(128),
    CONSTRAINT ck_protection_incident_clear CHECK (
        (status = 'open' AND cleared_at_utc IS NULL)
        OR (status = 'cleared' AND cleared_at_utc IS NOT NULL)
    )
);
CREATE INDEX IF NOT EXISTS ix_protection_incidents_agent_time ON protection_incidents(agent_id, triggered_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_protection_incidents_open ON protection_incidents(triggered_at_utc DESC) WHERE status = 'open';

COMMIT;
