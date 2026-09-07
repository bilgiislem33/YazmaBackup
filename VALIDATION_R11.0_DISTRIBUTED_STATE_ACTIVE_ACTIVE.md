# R11.0 — Distributed State Engine + Active/Active Control Plane

R11.0 removes the R10.5 restriction that only one Control Plane process may be production-ready.

## Distributed commit coordinator
Every `StateStore` commit now:
1. acquires an OS/filesystem exclusive writer lease (`FileShare.None`) in the shared state root,
2. reads `distributed-state.version`,
3. compares it with the node's in-memory state version,
4. rejects stale-node commits with `DistributedStateConflictException`,
5. atomically writes the state document,
6. increments the distributed state version,
7. releases the writer lease.

A stale concurrent mutation is never silently allowed to overwrite newer state. The API returns HTTP `409 Conflict` with `Retry-After: 1`.

## Active/active role
`YAZMABACKUP_HA_ROLE=active-active` makes both nodes readiness-capable and mutation-capable. HA still requires certificate-protected shared Data Protection.

## Safety boundary
This is a major architectural transition, but it is **not described as a relational distributed database**. The durable domain state remains the existing state document. R11.0 adds distributed serialization and optimistic conflict detection around it so two Control Plane nodes cannot silently perform last-writer-wins commits.

A later database migration can move domain aggregates to PostgreSQL/SQL Server without changing Agent protocol.

## Agent impact
No Agent runtime/protocol change.
Exact deployment identity, repository key auto-provision, Global NAS, backup/restore format and Agent DPAPI stores are unchanged.
