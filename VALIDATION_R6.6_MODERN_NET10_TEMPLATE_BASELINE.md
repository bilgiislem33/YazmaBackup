# R6.6 — Modern .NET 10 / C# 14 Template Baseline

## Current stack
YazmaBackup already targets `net10.0` with C# 14 and ASP.NET Core Minimal Hosting.

## Modern template baseline applied
- Added `YazmaBackup.slnx` while keeping the existing `.sln` for compatibility.
- Kept deterministic `.NET 10.0.100` SDK baseline with `rollForward: latestFeature` and prerelease disabled.
- Kept `TargetFramework=net10.0` and `LangVersion=14.0`.
- Explicitly enabled built-in .NET analyzers.
- Added repository-wide `.editorconfig` for modern C# conventions.
- Added `scripts/CHECK_TEMPLATE_BASELINE.ps1`.
- Preserved `Microsoft.NET.Sdk.Web` for Control Plane and `Microsoft.NET.Sdk` for libraries/Agent.
- Preserved ASP.NET Core top-level `WebApplication.CreateBuilder` / Minimal API hosting.

## Deliberately not changed
- No new UI framework was introduced.
- No external NuGet dependency was added.
- No Agent protocol, backup engine, repository schema, policy state, enrollment identity, zero-touch lifecycle, security route, or runtime behavior was modified.

## Verification note
The package pins the stable .NET 10/C#14 baseline already used by the project instead of switching to prerelease SDKs.
