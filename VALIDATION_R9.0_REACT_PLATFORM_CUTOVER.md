# R9.0 — React Platform Cutover Candidate

## Large transition
The new TypeScript + React + Next.js + Tailwind + shadcn-style + Tremor console now covers the high-value production workflows that previously required the legacy Vanilla UI.

### Operational React coverage
- Dashboard and operational health
- Computer fleet + editable Kullanıcı / Sahibi
- Backup policy create / enable / disable / delete
- Backup history with real error/detail drill-down
- Restore enqueue with overwrite confirmation
- Repository health
- Global NAS profile save + blank-password reuse behavior
- Alarm acknowledge, assignment, note, due date and bulk workflow
- MeshCentral connector save/test/sync, live inventory and exact NodeId deployment
- Recovery Plan creation + Recovery Run history
- Recovery Runbook creation + run history
- Agent signed stage-update/apply workflow
- Management user creation + RBAC + enable/disable
- Audit
- Settings encrypted export/import
- HMAC HTTPS notification routes

## Runtime preservation
No change to:
- .NET 10 Control Plane backend behavior
- Agent protocol/runtime
- exact deployment NodeId/DeploymentId/AgentId binding
- R6.7 heartbeat-aware MeshCentral timeout reconciliation
- R6.8 repository-key auto-provision/self-heal
- R6.9 persistent Global NAS profile and RSA-OAEP Agent fanout
- R7.0 encrypted settings transfer
- repository format or encryption keys

## Cutover posture
The React console is now the primary UI candidate. `legacy-ui` remains in source as a non-destructive emergency rollback until the Windows npm/typecheck/Next production build and field UI validation are completed.
