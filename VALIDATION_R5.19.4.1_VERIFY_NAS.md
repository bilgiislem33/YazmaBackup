# VALIDATION_R5.19.4.1_VERIFY_NAS

## Field finding
R5.19.4 reached [12e/23] and stopped on `NAS credential DPAPI store invariant eksik.`

## Root cause
This was a VERIFY false positive, not a NAS runtime defect.
`NasCredentialStore.cs` correctly uses `AgentPaths.NasCredentialDirectory`.
The literal folder name `nas-credentials` is defined in `AgentPaths.cs`, so checking for that literal
inside `NasCredentialStore.cs` was architecturally incorrect.

## Correction
VERIFY now checks:
- `MachineSecretStore` in `NasCredentialStore.cs`
- `NasCredentialDirectory` reference in `NasCredentialStore.cs`
- literal `nas-credentials` definition in `AgentPaths.cs`

No Agent, Control Plane, SMB, DPAPI, RSA transport, backup, restore or database runtime logic changed.
