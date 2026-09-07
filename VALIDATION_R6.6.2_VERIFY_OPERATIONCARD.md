# R6.6.2 — VERIFY Operation Card Gate Fix

## Field failure
Windows PowerShell stopped at `[12l/23]` with:
`"dashboard:\\[" - Sonlandırılmayan [] kümesi.`

## Root cause
The R6.3 VERIFY gate built a regular expression dynamically using `':\\['`.
Under .NET regular-expression parsing this produced an invalid expression for the intended literal `[` test.

## Permanent correction
The gate no longer uses regex for this check. It now performs an exact literal string test:

`$webJs.Contains($module + ':[')`

This directly verifies the JavaScript operation-card map keys and cannot fail because of regex escaping.

## Scope
VERIFY-only correction. No Agent runtime, Control Plane runtime, UI behavior, backup engine, policy state, enrollment identity, or zero-touch lifecycle change.
