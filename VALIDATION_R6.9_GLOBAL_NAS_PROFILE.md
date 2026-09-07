# R6.9 — Persistent Global NAS Profile

## Goal
NAS settings should be entered once, stay durable, and automatically apply to all computers. The password should only be re-entered when it actually changes.

## Implementation
- Added a Control Plane global NAS profile protected with ASP.NET Core Data Protection.
- Persistent fields: repository ID, UNC path, NAS username, encrypted NAS password, profile version and update time.
- Password is never returned to the browser.
- Saving with a blank password reuses the already protected password.
- Saving/changing the profile fans the credential out to all Agents with RSA-OAEP-SHA256 wrapping.
- Each Agent receives an idempotency key including the global profile version; unchanged settings are not repeatedly reprovisioned.
- New/re-enrolled Agents automatically receive the current global profile on authenticated heartbeat.
- Agent continues to store NAS credentials using DPAPI LocalMachine.

## Security
No plaintext password in localStorage, no browser password persistence, no identity reset, no Agent runtime change.
