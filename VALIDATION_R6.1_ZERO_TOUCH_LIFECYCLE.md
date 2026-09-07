# R6.1 Zero-Touch Agent Lifecycle

- Baseline: user-provided R6 Big Cards package.
- Existing backup policies remain Control Plane state and are not recreated during Agent upgrade.
- Agent identity/token/config/repository keys/NAS credentials remain under ProgramData and are preserved by in-place install.
- Existing StageAgentUpdate cryptographic verification remains mandatory.
- Added ApplyStagedAgentUpdate command. It only applies a previously staged and signature-verified package.
- Installer's existing service health validation and ImagePath rollback remain in the upgrade path.
- Added big-card Agent Yaşam Döngüsü UI and fleet lifecycle API.

Important: automatic fleet rollout still requires publishing/signing an Agent package. This release provides the endpoint apply mechanism and persistent lifecycle foundation; it does not bypass update signatures.
