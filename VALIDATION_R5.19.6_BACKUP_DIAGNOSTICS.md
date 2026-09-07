# VALIDATION_R5.19.6_BACKUP_DIAGNOSTICS

## Field evidence addressed
Field logs showed repeated `BackupPath` commands completing with `succeeded:false` but without the exception text in the Agent log.
The same Agent later received repeated HTTP 503 responses approximately every polling interval.

## Corrections
- Agent emits `command-failed` with command id, command type, exception type, sanitized error, source path, repository root and repository id.
- `command-completed` includes the sanitized error for failed commands.
- `/api/v1/admin/backup-history` now exposes original source path/repository details and classifies the failure.
- Backup History UI has a safe expandable error/detail cell containing error, source, repository, NAS path and command id.
- HTTP 502/503/504 are handled as temporary Control Plane/reverse-proxy unavailability with bounded exponential backoff up to 120 seconds.
- Existing authentication recovery, exact deployment binding, NAS credential handling and backup engine behavior are preserved.

This release improves diagnosis and resilience. It does not hide or convert a real storage/source/VSS/encryption failure into a false success.
