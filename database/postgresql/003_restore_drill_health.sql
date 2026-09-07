BEGIN;

ALTER TABLE backup_policies
    ADD COLUMN IF NOT EXISTS restore_drill_interval_days integer NOT NULL DEFAULT 7
        CHECK (restore_drill_interval_days BETWEEN 1 AND 365),
    ADD COLUMN IF NOT EXISTS last_restore_drill_scheduled_at_utc timestamptz,
    ADD COLUMN IF NOT EXISTS next_restore_drill_at_utc timestamptz;

UPDATE backup_policies
SET next_restore_drill_at_utc = now() + make_interval(days => restore_drill_interval_days)
WHERE next_restore_drill_at_utc IS NULL;

ALTER TABLE backup_policies
    ALTER COLUMN next_restore_drill_at_utc SET NOT NULL;

CREATE INDEX IF NOT EXISTS ix_backup_policies_restore_drill_due
    ON backup_policies(enabled, next_restore_drill_at_utc)
    WHERE enabled = true;

COMMIT;
