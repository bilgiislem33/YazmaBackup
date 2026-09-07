# R10.2 — Production Reliability & Observability

## Reliability Center
New React module aggregates existing real Control Plane APIs for:
- operational health score
- backup success SLO
- Agent online ratio
- repository health/capacity
- alarm severity
- cluster scheduler leadership
- MeshCentral fleet health
- audit failures with correlation IDs

## Correlation / latency
Every Control Plane response now emits:
- `X-YazmaBackup-Correlation-ID` using ASP.NET `TraceIdentifier`
- `Server-Timing: app;dur=...`

Frontend API errors surface the correlation ID and apply a 30-second request timeout.

## Production smoke test
`scripts/PRODUCTION_SMOKE_TEST.ps1` validates:
- public React UI HTTP 200
- `_next` static asset references
- correlation response header
- Server-Timing header
- session endpoint behavior

## Runtime impact
Control Plane middleware has a minimal observability-only change. Agent runtime, Agent protocol, repository format, backup engine, MeshCentral exact deployment binding, R6.8 AutoKey, R6.9 Global NAS and R7.0 settings transfer are unchanged.
