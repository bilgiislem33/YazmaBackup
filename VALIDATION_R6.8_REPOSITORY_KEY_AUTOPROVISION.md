# R6.8 — Repository Encryption Key Auto-Provision

## Field symptom
NAS credential Save+Test and stored-credential access succeed, but Backup/Policy execution falls into `Şifreleme Anahtarı` because the Agent repository key ring is missing.

## Root cause
NAS SMB credentials and repository encryption keys are independent security objects.
The manual backup endpoint already queued `ProvisionRepositoryKey` before `BackupPath`, but scheduled policy execution in `EnqueueDueBackupPoliciesAsync` queued only `BackupPath`. Existing/legacy policies could therefore reach the Agent without an encrypted repository key ring.

## Correction
- Policy scheduler now pre-provisions the repository key before due policy backups are queued.
- Repository key is created/retrieved from the existing Control Plane `RepositoryKeyVault`.
- It is wrapped with the Agent RSA key using RSA-OAEP-SHA256.
- Agent still stores the key ring protected by DPAPI.
- A one-shot server-side self-heal catches the exact legacy error `Repository key ring is not provisioned`, queues key provisioning, then requeues the exact failed BackupPath once.
- Self-heal retry is idempotency-tagged `autokey:` to prevent loops.

## Safety
No NAS password persistence change, no key reset, no plaintext repository key transport, no identity deletion, no Agent runtime change.
