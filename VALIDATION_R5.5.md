# YazmaBackup v1.2.0 R5.5 Validation

## Bootstrap-safe logging invariants
1. Remote MeshCentral bootstrap MUST NOT create `C:\ProgramData\YazmaBackup\Logs` before `INSTALL_AGENT.ps1` runs.
2. The first installer log MUST be written inside the isolated deployment workspace.
3. Failure evidence MUST be copied best-effort to `C:\ProgramData\YazmaBackupDeployLogs`.
4. Canonical `C:\ProgramData\YazmaBackup\Logs` creation/copy occurs only after installer success and endpoint evidence checks.
5. R5.4 root-first ACL recovery remains required.

## Field acceptance
- Endpoint with a deliberately denied legacy `YazmaBackup\Logs` ACL can self-heal without manual ACL edits.
- Deployment reaches `succeeded`, service is Running, and identity/token evidence exists.
- On failure, UI surfaces detailed error and fallback log path.
