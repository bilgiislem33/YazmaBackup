# R8.0 — Next.js Enterprise Frontend Migration

## New frontend
- TypeScript + React + Next.js App Router.
- Tailwind CSS 4.
- Source-owned shadcn/ui-style primitives.
- Tremor dashboard widgets.
- Recharts dependency ready for advanced visualizations.
- Lucide icon system.
- Static export: ASP.NET Core remains the only production backend/runtime.

## Migrated in R8.0
- Authentication/login shell.
- Enterprise sidebar/topbar.
- Dashboard with real `/api/v1/admin/dashboard` and `/operational-health` data.
- Computer fleet cards using real `/agents`.
- Policies table using real `/policies`.
- Global NAS summary using real `/nas/global-profile`.
- Settings export/import using existing R7.0 APIs.
- Responsive navigation.

## Safety
- Agent, backup engine, repository format, identity binding, NAS credential flow, repository key flow and MeshCentral deployment runtime are unchanged.
- Original R7.2 UI is preserved under `src/YazmaBackup.ControlPlane/legacy-ui`.
- `ROLLBACK_FRONTEND.ps1` restores the legacy UI only; it does not touch state/database.
- Production backend remains .NET 10; Next.js is exported as static frontend assets.

## Not yet fully migrated
- Restore operational forms, lifecycle, validation, recovery, alarms, users/security/audit, Mesh administration and advanced operations still need React-native screens.
- The legacy UI remains available as a fallback while these modules are moved.
