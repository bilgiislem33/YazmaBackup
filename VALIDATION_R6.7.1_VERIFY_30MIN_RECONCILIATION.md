# R6.7.1 — VERIFY 30-Minute Reconciliation Gate Fix

## Field failure
Windows PowerShell stopped at `[12p/23]` with:
`MeshCentral 30-minute terminal reconciliation invariant eksik.`

## Root cause
The actual R6.7 runtime code is correct and contains both:
- `TimeSpan.FromMinutes(30)`
- the active deployment status chain `queued / dispatching / dispatched / installing`

The VERIFY gate incorrectly escaped parentheses in a PowerShell/.NET regular expression as `\\(` and `\\)`, causing a false negative.

## Permanent correction
The R6.7 VERIFY gate now uses exact literal `.Contains()` checks instead of regular expressions for these source-code invariants.

## Scope
VERIFY-only correction. R6.7 MeshCentral runtime behavior, exact deployment binding, 30-minute reconciliation window, Agent runtime and UI behavior are unchanged.
