# R15.0 — Direct SQL Mutation Core Cutover

R15 begins the write-side cutover without replacing all mutation behavior at once.

## Focused SQL mutation paths
The following hot operations now use focused PostgreSQL transactions when PostgreSQL state is active:
- Agent command enqueue
- Backup Policy create
- Backup Policy enable/disable
- Backup Policy delete

Each operation:
1. opens a PostgreSQL `Serializable` transaction,
2. atomically CAS-updates the R12 compatibility state row using expected version,
3. upserts/deletes only the affected normalized row,
4. commits both changes together.

If the state version changed on another node, the transaction rolls back and the existing distributed-state conflict path reloads current state and returns the established HTTP 409 / Retry-After semantics.

This removes the R13 full normalized projection rewrite from these high-frequency mutation paths.

## Rollback
Set `YAZMABACKUP_DIRECT_SQL_MUTATIONS=false` to route these operations back through the compatibility full-projection commit path. No schema downgrade or Agent change is required.

## Deliberate scope
Command claim/lease/complete and scheduled-policy multi-entity mutations still use the compatibility transactional commit in R15.0 because they combine pruning, leasing, resilience and scheduler side effects. Moving those safely is the next write-cutover phase.

## Safety preserved
- PostgreSQL Serializable transaction
- state version compare-and-swap
- command idempotency unique index
- Agent foreign keys
- R14 direct SQL reads
- R13 normalized schema
- R12 stale-node fencing
- Agent protocol unchanged
