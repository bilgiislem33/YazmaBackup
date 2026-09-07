# YazmaBackup v1.2.0 R5.6 Validation — Native ACL Recovery Determinism

R5.6 closes the field failure where Windows PowerShell 5.1 promoted `icacls.exe` stderr to a terminating `NativeCommandError` while `$ErrorActionPreference='Stop'`, bypassing the installer's intended `AllowNonZero`/exit-code logic.

## Acceptance gates

- `INSTALL_AGENT.ps1` invokes ACL recovery native tools through `System.Diagnostics.ProcessStartInfo` with redirected stdout/stderr.
- No ACL recovery path depends on `& icacls.exe ... 2>&1` or `$LASTEXITCODE` after a native stderr record.
- Recursive `/T /C` recovery steps are explicitly best-effort; their non-zero exit codes cannot abort before the mandatory root write/read/delete preflight.
- Root ACL repair remains strict and root-first.
- Critical identity files remain individually repaired/validated.
- The mandatory create/write/read/delete probe remains the final ACL safety gate before Agent bootstrap.
- R5.5 bootstrap-safe fallback logging remains preserved.
- Windows `scripts\\VERIFY.ps1` and Release build remain required before production promotion.

## Field regression

Re-run MeshCentral one-click installation against an endpoint containing the previously damaged `C:\\ProgramData\\YazmaBackup` tree. The deployment must no longer fail with the raw PowerShell-formatted message `icacls.exe : ... Access denied`. If recursive recovery reports inaccessible stale descendants, the installer must continue to the strict root preflight and either proceed or fail with a YazmaBackup-owned diagnostic including operation + exit code.
