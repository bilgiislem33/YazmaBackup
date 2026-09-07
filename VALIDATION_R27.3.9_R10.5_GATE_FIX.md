# R27.3.9 — R10.5 Current HA Behavior Gate

Windows field VERIFY reached and passed R10.4. R10.5 then failed because its verifier still required the historical literal `dpapi-local-machine`.

The current HA verifier now checks the behavior-bearing source tokens that are actually present:
- ReadyForTraffic
- AcceptsMutations
- portable
- HA role and certificate configuration
- ProtectKeysWithCertificate
- readiness/status/drain/undrain endpoints

Agent runtime is unchanged.
