# VALIDATION_R5.19.5_NAS_SAVE_AND_TEST

## Problem addressed
The UI could show `Kimlik: Windows mevcut kimliği` when the operator believed a NAS credential had been saved.
That left ambiguity between an old Agent, a failed credential-provision command, and an SMB authentication failure.

## New deterministic flow
- New `ProvisionAndTestNasCredential` Agent command.
- Control Plane sends username/password RSA-OAEP-SHA256 wrapped to the selected Agent.
- Agent opens the SMB session immediately with exactly that credential.
- Agent tests repository existence and a create/delete write probe.
- Credential is stored with Windows DPAPI only after the live SMB test succeeds.
- Failure result includes Windows error code and resolved share root.
- UI primary action is now `Şifreyi Kaydet ve NAS’ı Test Et`.

This eliminates the previous ambiguous `Windows mevcut kimliği` state for first-time configuration.
