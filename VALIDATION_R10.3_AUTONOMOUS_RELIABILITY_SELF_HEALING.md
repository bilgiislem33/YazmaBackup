# R10.3 — Autonomous Reliability & Self-Healing

## Autonomous decision engine
A new read-only Control Plane endpoint classifies:
- offline Agents
- overdue policies
- failed backups
- protection locks

It calculates:
- autonomy/reliability score
- backup success percentage
- Agent online percentage
- critical/warning incident counts
- canary/rollout guard PASS/HOLD

## Safe self-healing
The React Autonomous Ops module can:
- enqueue self-healing diagnosis
- propose allowlisted repository circuit reset
- require explicit operator confirmation before any mutation

Safety policy is explicit:
- autonomous diagnosis: enabled
- silent mutation: disabled
- mutation approval: required
- mutation allowlist: existing `reset-repository-circuit` and `cleanup-stale-restore-temp`

No identity reset, repository-key reset, NAS credential reset, ACL mutation or silent destructive action was added.

## Runtime impact
Control Plane runtime adds a read-only decision endpoint. Existing self-healing command endpoints are reused. Agent runtime/protocol is unchanged.
