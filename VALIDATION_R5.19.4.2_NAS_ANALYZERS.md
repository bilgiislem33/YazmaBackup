# VALIDATION_R5.19.4.2_NAS_ANALYZERS

## Field VERIFY failure
R5.19.4.1 passed [12e/23] and failed only at [14/23] Release build because warnings-as-errors promoted:
- CA1416 on MachineSecretStore/Read/Write
- CA1822 on NasCredentialStore.Exists

## Root cause
NasCredentialStore uses Windows DPAPI through MachineSecretStore, so the class must explicitly declare
Windows platform support. Exists does not use instance state and should be static.

## Correction
- Added `[SupportedOSPlatform("windows")]` to NasCredentialStore.
- Added `System.Runtime.Versioning` import.
- Changed `Exists` to static and updated the AgentWorker call site.

No SMB protocol, RSA transport, DPAPI format, repository behavior, backup/restore behavior or API contract changed.
