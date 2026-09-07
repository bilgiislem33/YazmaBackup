# R10.5 — Enterprise Control Plane HA + Zero-Downtime Upgrade Foundation + Disaster Recovery

## HA model
R10.5 introduces a fail-closed **active/standby** Control Plane topology.

Supported roles:
- `single`
- `active`
- `standby`

A standby node:
- returns live health
- returns `503` for readiness
- blocks mutating `/api` traffic
- does not register the background mutation services
- remains available for preflight/read-only diagnostics

This release intentionally does **not** claim active/active writes. The existing Control Plane state engine is JSON/file based and is not a multi-writer distributed database. R10.5 therefore uses active/standby semantics rather than risking split-brain state corruption.

## Shared Data Protection
HA mode requires `YAZMABACKUP_DP_CERT_THUMBPRINT`.
The certificate must exist in `LocalMachine\My` and include the private key on each HA node.

Data Protection keys remain under the configured shared state root and are protected by that certificate. Without the certificate, HA mode fails at startup.

Single-node mode remains compatible with Windows DPAPI LocalMachine.

## Readiness and drain
New endpoints:
- `GET /health/live`
- `GET /health/ready`
- `GET /api/v1/admin/ha/status`
- `POST /api/v1/admin/ha/drain`
- `POST /api/v1/admin/ha/undrain`
- `GET /api/v1/admin/dr/inventory`

Drain blocks new mutation traffic and makes readiness fail, allowing a reverse proxy/load balancer to remove the node before maintenance.

## Controlled switchover
`scripts/HA_SWITCHOVER.ps1` implements a fail-closed runbook:
1. validate candidate live health
2. drain current active
3. promote/restart candidate by service configuration
4. require candidate `/health/ready` HTTP 200
5. only then retire the previous active

Because this product still uses a single-writer state store, R10.5 does not run both nodes mutation-active at the same time. This provides zero-downtime **upgrade readiness for read traffic and controlled near-zero mutation cutover**, without unsafe split-brain claims.

## Disaster Recovery
`CREATE_DR_SNAPSHOT.ps1` creates a CMS certificate-encrypted `.ybdr.p7m` bundle containing the critical Control Plane state:
- `control-plane-state.json`
- state backup
- Data Protection key ring
- repository encryption key vault
- Global NAS protected profile
- autonomous orchestration state
- recovery evidence signing key

A `DR_MANIFEST.json` records SHA-256 and length for each file.

`RESTORE_DR_SNAPSHOT.ps1`:
- decrypts with the certificate private key
- validates every SHA-256
- defaults to validate-only
- requires explicit offline confirmation before changing state
- creates a safety copy of the existing target state before apply

The DR bundle does not export the certificate private key. The restore node must receive the same certificate through the organization's certificate/secret-management process.

## Runtime impact
Control Plane runtime: changed.
Agent runtime/protocol: unchanged.
Repository format: unchanged.
Repository encryption keys are preserved, not regenerated.
Global NAS credential flow is unchanged.
Exact Agent identity/deployment binding is unchanged.
