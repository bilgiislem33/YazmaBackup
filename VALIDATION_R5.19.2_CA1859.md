# VALIDATION_R5.19.2_CA1859

## Field VERIFY failure
R5.19.1 passed gates [1/23] through [13/23] and failed only during [14/23] Release build because
warnings-as-errors promoted analyzer rule CA1859 in `YazmaBackup.Agent/AgentWorker.cs`.

## Correction
`BuildRestoreEntries` now returns the concrete `RestoreEntryDto[]` type instead of
`IReadOnlyList<RestoreEntryDto>`.

## Scope
Compile-time type refinement only. Restore Explorer behavior, Agent command contract, API payload,
backup engine, MeshCentral deployment binding, enrollment recovery, Windows safe traversal and
security behavior are unchanged.
