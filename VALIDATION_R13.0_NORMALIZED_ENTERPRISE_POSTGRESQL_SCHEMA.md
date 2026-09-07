# R13.0 — Normalized Enterprise PostgreSQL Schema

## What changed
R13.0 adds relational hot-aggregate tables beside the R12 transactional PostgreSQL state row:

- `yb_agents`
- `yb_commands`
- `yb_backup_policies`
- `yb_audit_events`
- `yb_alarms`
- `yb_schema_migrations`

Every successful Control Plane state commit updates the authoritative R12 state row **and** these normalized tables inside the same PostgreSQL transaction. If the normalized projection fails, the state commit also rolls back. A successful commit therefore cannot leave the normalized projection behind.

## Relational controls
The schema includes:
- composite cluster-scoped primary keys
- Agent foreign keys for Commands and Backup Policies
- cascade cleanup where appropriate
- idempotency uniqueness for Agent commands
- due-policy index
- pending-command partial index
- command type/completion index
- Agent last-seen and machine-name indexes
- Audit time, actor, correlation and BRIN indexes
- Alarm status/severity and Agent indexes
- explicit schema migration record `13.0`

## Operational visibility
`/api/v1/admin/state-engine/status` now reports:
- normalized schema version
- normalized Agents count
- Commands count
- Policies count
- Audit Events count
- Alarms count

The React HA/DR module shows the same counts.

## Migration behavior
When PostgreSQL is first initialized from `control-plane-state.json`, R13 immediately builds the normalized projection in the same bootstrap transaction. Existing PostgreSQL state is also projected during startup without resetting business state.

## Scope boundary
R13.0 is a **transactionally consistent normalized schema foundation**, not the final removal of the compatibility aggregate. Mutation logic still materializes `StateDocument` in memory and persists the R12 JSONB compatibility snapshot while the normalized hot aggregates are updated in the same transaction.

The next cutover can move high-volume reads and then individual mutation aggregates directly to normalized SQL tables without changing Agent protocol or repository format.

## Agent impact
Agent runtime and protocol are unchanged. No Agent publish is required.
