# R10.4 — Autonomous Remediation Orchestrator + Canary Rollout Controller

## Persistent remediation state machine
State is stored in `autonomous-orchestration.protected` using ASP.NET Core Data Protection.

Remediation flow:
`queued-diagnosis → diagnosing → awaiting-approval → approved → applying → verifying → completed`

`diagnose-only` plans complete after the diagnosis command and never mutate the Agent.
Mutating remediation supports only the existing allowlist:
- `reset-repository-circuit`
- `cleanup-stale-restore-temp`

Mutation requires an explicit security-admin approval endpoint. No silent mutation was added.

## Canary rollout state machine
Rollout flow:
`draft → preflight → canary-staging → canary-applying → canary-observing → wave-staging → wave-applying → wave-observing → completed`

Safety controls:
- deterministic canary selection
- configurable canary percentage
- configurable max parallel wave size
- configurable observation window
- minimum reliability score
- minimum backup success percentage
- minimum Agent online percentage
- protection-lock fail-closed
- backup failure in active wave causes HOLD
- operator HOLD / RESUME / CANCEL
- deterministic idempotency keys for stage/apply commands
- cluster lease so only one Control Plane node advances the state machine

## Rollback semantics
The Agent update engine only accepts newer versions. R10.4 therefore does **not** add an unsafe central downgrade command.
Rollback protection remains:
- during installation, `INSTALL_AGENT.ps1` health-check can restore the previous Windows service `ImagePath`
- after a successful canary install, a health-gate failure stops all subsequent waves with `HOLD`
- an already healthy/accepted new version is not silently downgraded

This is fail-closed and matches the current Agent updater's real capability.

## Runtime impact
Control Plane runtime changes: YES.
Agent runtime/protocol changes: NO.
Repository format/key management/NAS credentials/exact deployment identity are unchanged.
