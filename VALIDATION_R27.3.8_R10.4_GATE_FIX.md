# R27.3.8 — R10.4 Behavior-Based Gate Fix

Windows field VERIFY proved the production React/Next.js chain is healthy:
- npm ci PASS
- TypeScript typecheck PASS
- next build PASS
- static export PASS
- R10.3 PASS

R10.4 then failed only because the verification script searched for the historical literal text `silent mutation`.

This release removes that wording-dependent check and verifies real behavior instead:
- mutating remediation enters `awaiting-approval`,
- `diagnose-only` is explicitly recognized,
- canary/wave states are present,
- health-gated rollout semantics remain,
- rollback remains local-installer-health-rollback,
- no automatic Agent downgrade command is introduced.

Agent runtime is unchanged.
