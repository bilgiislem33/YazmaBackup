# R8.1 — Full React Module Migration

## React surfaces added
Operations, Validation, Recovery, Alarms, Agent Lifecycle, MeshCentral, Users, Security and Audit.

## Data integrity
- Operations/Audit/Users use existing live Control Plane APIs.
- Validation and Mesh surfaces derive views from real command history.
- Security uses real Agent protection state.
- No fake operational success state is generated.

## Safety
- .NET 10 backend unchanged.
- Agent runtime/protocol unchanged.
- R6.7 Mesh timeout, R6.8 repository key self-heal, R6.9 Global NAS and R7.0 settings transfer preserved.
- Legacy R7.2 UI remains available for rollback/fallback.
- No repository key reset, Agent identity reset or state migration.
