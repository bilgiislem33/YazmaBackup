# R6.7 — MeshCentral Heartbeat-Aware Deployment Timeout

## Field symptom
A deployment was marked `failed` exactly five minutes after dispatch with:
`MeshCentral deployment command timed out after 5 minutes.`

## Root cause
`meshctrl RunCommand --reply` can remain open while the remote PowerShell installer is still running. The previous worker treated cancellation of that reply wait as proof that installation failed.

## Correction
- The 5-minute meshctrl reply timeout is now a transport wait boundary, not a terminal failure.
- On timeout, deployment remains active as `installing` and waits for exact Agent registration + authenticated heartbeat.
- Exact DeploymentId/NodeId/AgentId correlation remains authoritative.
- The existing 30-minute reconciliation window remains the real terminal deadline.
- Stale `dispatching` after worker restart moves into reconciliation instead of immediate failure or duplicate install.
- Explicit MeshCentral/installer errors still fail immediately.
- UI displays `Agent Kaydı Bekleniyor` during reconciliation.

## Safety
No hostname-only regression, no identity reset, no enrollment bypass, no signature bypass, no Agent runtime change.
