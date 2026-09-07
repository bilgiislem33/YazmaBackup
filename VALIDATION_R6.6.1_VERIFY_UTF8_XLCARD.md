# R6.6.1 — VERIFY UTF-8 / XL Card Gate Fix

## Field failure
Windows PowerShell 5.1 stopped at `[12k/23]` with:
`Universal XL card CSS invariant eksik.`

## Root cause
The UI/runtime was correct. `styles.css` is UTF-8 without BOM. Windows PowerShell 5.1 can decode `Get-Content` without an explicit encoding through the legacy Windows code page. The R6.2/R6.5 VERIFY gates compared Unicode release-comment text containing an em dash, so the gate could fail even though the actual XL-card CSS existed.

## Permanent correction
- VERIFY now reads Control Plane `styles.css`, `index.html`, and `app.js` explicitly with `-Encoding UTF8`.
- R6.2 and R6.5 gates validate structural CSS selectors/tokens instead of decorative Unicode release comments.
- No Control Plane runtime, Agent runtime, backup engine, policy state, UI behavior, or CSS behavior changed.

This is a VERIFY-only correction.
