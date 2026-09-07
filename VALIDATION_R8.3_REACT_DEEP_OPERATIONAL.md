# R8.3 — React Deep Operational Migration

## Operational workflows now in React
- Restore enqueue via the existing Agent restore API, including explicit overwrite confirmation.
- MeshCentral device selection and exact NodeId deployment.
- Recovery Plan creation from real Backup Policy IDs.
- Management user creation with valid YazmaBackup RBAC roles and forced first-login password change.
- Agent staged update and apply controls with explicit apply confirmation.

## Runtime preservation
No Control Plane backend implementation, Agent protocol, repository format, exact Agent identity binding, NAS credential flow, repository-key flow, or MeshCentral timeout/reconciliation behavior changed.
