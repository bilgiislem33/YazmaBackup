# R14.0 — Direct SQL Read Cutover

R14 moves production read traffic away from the in-memory compatibility `StateDocument` for the highest-volume operational paths.

Direct normalized SQL reads now cover:
- Agents
- single Command lookup
- recent Commands
- Backup Policies
- Audit Events
- Alarms
- operational backup/restore command metrics

In PostgreSQL mode direct reads are enabled by default. Emergency rollback is available with:
`YAZMABACKUP_DIRECT_SQL_READS=false`

The R12 transactional JSONB aggregate remains the mutation compatibility boundary in R14. That means writes still use the proven StateStore mutation model and commit the normalized projection atomically. R14 therefore removes a major scalability bottleneck without simultaneously changing every mutation path.

The state-engine status reports `normalizedReadPath=direct-sql`, and the HA/DR React screen exposes the active read path.

Agent runtime/protocol is unchanged.
