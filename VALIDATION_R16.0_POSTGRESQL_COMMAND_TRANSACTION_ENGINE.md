# R16.0 — PostgreSQL Command Transaction Engine

## Production cutover
R16 moves command ownership from in-memory selection to PostgreSQL row locking.

`ClaimNextAsync` now routes to the database when direct SQL mutations are enabled. PostgreSQL selects the oldest eligible command with:

`FOR UPDATE SKIP LOCKED`

This prevents two active Control Plane nodes from claiming the same command row.

## Lease state
`yb_commands` now has migration-safe columns:
- `lease_id`
- `lease_expires_at_utc`
- `last_lease_renewal_utc`

Existing R13/R14/R15 databases are upgraded with `ADD COLUMN IF NOT EXISTS`.

Indexes:
- `ix_yb_commands_claimable`
- `ix_yb_commands_lease_expiry`

## Claim transaction
A claim transaction:
1. locks the authoritative cluster state row,
2. prunes expired completed-command retention rows,
3. marks commands that consumed MaxAttempts as failed,
4. selects one eligible command with `FOR UPDATE SKIP LOCKED`,
5. creates a new lease ID and expiry,
6. increments AttemptCount,
7. updates normalized command payload and compatibility state snapshot,
8. advances the state version only when something changed,
9. commits atomically.

Expired leases become eligible for a later claim until MaxAttempts is consumed.

## Lease renewal
Renewal locks the authoritative state row and exact command row, verifies AgentId + LeaseId + non-expired ownership, extends the lease and updates normalized + compatibility state atomically.

## Completion boundary
Command completion deliberately remains on the full transactional projection path in R16.0. Completion can invoke resilience logic that mutates alarms, repository circuits and other aggregates. Moving completion to a command-only SQL update would silently lose those side effects. This is intentional safety, not unfinished wiring.

## Rollback
`YAZMABACKUP_DIRECT_SQL_MUTATIONS=false` returns Claim/Renew and R15 focused mutations to the compatibility transaction path without schema downgrade.

## Agent
Agent runtime and protocol are unchanged. No Agent publish is required.
