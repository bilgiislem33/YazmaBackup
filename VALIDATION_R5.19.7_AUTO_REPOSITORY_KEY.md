# VALIDATION_R5.19.7_AUTO_REPOSITORY_KEY

## Field root cause
Agent logs reported:
`InvalidOperationException: Repository key ring is not provisioned: RENAULT_NAS.`

## Permanent correction
- Added a Control Plane `RepositoryKeyVault`.
- A stable random 256-bit key is created once per RepositoryId.
- Vault material is protected with ASP.NET Core Data Protection; on Windows the existing Data Protection key ring is DPAPI protected.
- Before each BackupPath command, Control Plane queues `ProvisionRepositoryKey` with the same stable key.
- The key is wrapped to the Agent's RSA public key with OAEP-SHA256.
- Agent queue ordering is FIFO by CreatedAtUtc, so key provisioning is claimed before the backup command.
- Re-provisioning the same key/key-id is idempotent in RepositoryKeyStore and repairs a reinstalled Agent automatically.

## Recovery requirement
The Control Plane state directory, including `dataprotection-keys` and `repository-key-vault`, is now cryptographic recovery material and must be included in server backup/DR procedures.
