# VALIDATION_R5.19_FUNCTIONAL_COMMAND_CENTER

## Functional leap
- Asset 360 tabs now render real selected-device data from Agents, policies, backup history, repository health and alarms.
- Backup LiveOps receives real stage, file-count, logical-byte progress and uses it for completion percentage and ETA; upload bytes/rate remain separate telemetry.
- Restore Explorer lists real directory/file entries from the encrypted backup manifest through a new Agent command. Clicking directories navigates the snapshot tree; selected paths can be added to existing granular restore.
- Alarm Center adds default SLA deadlines, persisted owner, operator note, filtering, selection and bulk acknowledge/resolve/assignment actions.

## Runtime convergence
This release also restores known-good runtime protections that had been lost in the later UI branch:
- exact MeshCentral deployment correlation by one-time enrollment token + AgentId
- stale credential self-healing re-enrollment
- Windows-safe backup/ransomware traversal using IgnoreInaccessible and ReparsePoint skip

## Deployment impact
Agent + Control Plane + Application + Contracts changed. After Windows VERIFY 23/23 PASS, rebuild the Agent package with `scripts\PUBLISH_AGENT.ps1 -Runtime win-x64`.
