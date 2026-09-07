# R12.0 — Transactional PostgreSQL State Engine + Production Active/Active

## Architectural transition
Production `active-active` now requires `YAZMABACKUP_STATE_ENGINE=postgresql`.

The existing file engine remains available for `single`, `active`, and `standby` compatibility, but R12 refuses to start a production active-active node on file state.

## Transactional state
PostgreSQL owns the authoritative Control Plane state row per `YAZMABACKUP_CLUSTER_ID`.

Each mutation performs an atomic compare-and-swap:
`UPDATE ... SET version=version+1 ... WHERE cluster_id=@cluster AND version=@expected RETURNING version`

This executes inside a PostgreSQL serializable transaction. If another node committed first, the stale node receives no updated row, reloads current database state, and returns the existing distributed-state HTTP 409 / Retry-After response instead of overwriting newer data.

## Safe migration/bootstrap
On first PostgreSQL startup:
- schema is created if absent
- current `control-plane-state.json` is serialized as bootstrap state
- `INSERT ... ON CONFLICT DO NOTHING` makes dual-node bootstrap idempotent
- existing PostgreSQL state always wins once initialized

No repository key, Agent identity, NAS credential, policy or audit state is intentionally reset.

## Scope honesty
The domain model is transactionally persisted in PostgreSQL JSONB in R12.0. It is now database-authoritative and transaction/fencing safe, but individual aggregates are **not yet normalized into relational tables**. A later schema-normalization release can split Agents, Commands, Policies, Audit and Alarms into dedicated tables without changing the Agent protocol.

## DR
Use both:
- `CREATE_DR_SNAPSHOT.ps1` for Data Protection/repository-key/security state
- `CREATE_POSTGRES_DR_SNAPSHOT.ps1` for transactional PostgreSQL domain state

The PostgreSQL dump is SHA-256 inventoried and CMS certificate encrypted. Database credentials are not written to command-line arguments by the DR script.

## Agent impact
Agent runtime/protocol unchanged. Do not republish Agent.
