# VALIDATION_R5.18_LIVE_DATA

## Functional UX leap
R5.17 presentation surfaces are now bound to existing production Control Plane data.

- Asset 360: real `/api/v1/admin/agents`, policies, backup history and repository-health data.
- Live Backup Ops: real `/api/v1/admin/transfer-telemetry` rate, active-transfer and transferred-byte data.
- Restore Explorer: real restore points returned by the existing Agent restore-points command.
- Capacity Forecast: real repository-health free/total bytes, status and estimated-days-to-full.
- Alarm Triage: real alarm severity/status data with live P1/P2/P3 counts.

## Safety
No new privileged API, schema migration, Agent protocol, backup format or restore format is introduced.
The release reuses existing authenticated admin APIs and existing restore command contracts.
