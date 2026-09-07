# R7.0 — Encrypted Settings Export / Import

## Export scope
- Global NAS profile, including NAS password.
- Computer user/owner assignments.
- Backup policies and retention/protection settings.

## Security
- Export file is encrypted with user-provided passphrase.
- PBKDF2-SHA256 (210,000 iterations) derives a 256-bit key.
- AES-256-GCM provides confidentiality and tamper detection.
- NAS password is never exported as plaintext.
- Wrong passphrase or modified package is rejected.

## Import behavior
- Computers are matched by exact machine name, case-insensitive.
- Assigned user metadata is restored.
- Existing equivalent policies are skipped rather than duplicated.
- Missing computers are reported.
- Global NAS profile is restored and automatically fanned out to all current Agents using the existing RSA-OAEP-SHA256 path.
- New Agents later receive the restored global NAS profile through the existing heartbeat auto-apply.

## Runtime
Control Plane/UI changed. Agent binary unchanged.
