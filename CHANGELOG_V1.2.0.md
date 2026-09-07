## R27.3.7 — Full React Gate Audit
- Fixed stale R10.3 React UI text gate after verified Next.js production build.
- Migrated R25 DR UI gate to the current R27.2 War Room and explicit approve/verify flow.
- Added a regression gate preventing these pre-cutover UI assumptions from returning.
- Agent unchanged.

## R27.3.3 — React Enterprise Pilot Center
- Restored Pilot Center as an actual React module instead of weakening the quality gate.
- Uses existing real readiness/template/bulk-apply/probe APIs.
- Migrated Pilot and Validation UI VERIFY checks from removed legacy HTML to React.
- Agent unchanged.

## R27.3.2 — React VERIFY Cutover Fix
- Removed stale pre-R26 `ControlPlane/wwwroot` reads from authoritative VERIFY.
- Replaced them with current React/Next single-source, safety, module, lifecycle and backend API/CSP gates.
- Did not restore legacy UI just to satisfy old tests.
- Runtime and Agent unchanged.

## R27.3.1 — Dependency + VERIFY Root-Cause Fix
- Removed incompatible @tremor/react 3.18.7 (React 18 peer) from the React 19.1.1 frontend.
- Replaced its limited dashboard use with native YazmaBackup Card/Progress UI.
- Replaced the broken blanket PackageReference rejection with an exact fail-closed allowlist for required Npgsql 10.0.0.
- No --force / --legacy-peer-deps workaround.
- Agent unchanged.

## R27.3 — Ultimate Enterprise Experience
- Added Executive Mode with real management-level protection, health, intelligence, recovery and alarm signals.
- Added compact sidebar and comfortable/compact density controls.
- Added unified premium motion/design polish and reduced-motion accessibility.
- Preserved R27.0 Command Center/NOC, R27.1 Fleet Galaxy/Device360 and R27.2 Recovery Timeline/DR War Room.
- No fake executive scores when backend data is unavailable.
- Agent unchanged.

## R27.2 — Recovery Experience + DR War Room
- Added real Recovery Evidence Timeline backed by recovery plans/runs.
- Added selected-run evidence metrics; no invented restore points.
- Upgraded DR Execution into an interactive War Room.
- Wired start/approve/verify/cancel UI actions to existing R25 safe DR APIs.
- Added dependency recovery rail and current-gate workflow.
- Preserved R27.0 Command Center/NOC, R27.1 Fleet Galaxy/Device360, R26 single-source React and R25 DB completion fence.
- Agent unchanged.

## R27.1 — Fleet Galaxy + Device 360
- Added interactive Fleet Galaxy using real Agent heartbeat/protection state.
- Added Device 360 side drawer with real Agent and active policy details.
- Explicitly labels Galaxy as a visual fleet map, not invented physical network topology.
- Preserves R27 Command Center/NOC and R26 single-source React contract.
- Agent unchanged.

## R27.0 — Frontend Experience / Command Center
- Added real-telemetry YazmaBackup Command Center.
- Added fullscreen NOC Mode.
- Added 5-second Live Operations command feed.
- Added Repository Radar and Live Activity Stream.
- Added fleet online, protection, active operation, alarm and repository risk metrics.
- Explicitly refuses to invent backup progress percentages when backend telemetry does not provide them.
- Removed misleading R10.0 Full React Cutover dashboard banner.
- Preserved R26 single React UI source and mandatory publish-chain contract.
- Agent runtime unchanged.

## R26.0 — Real React/Next.js Production Cutover
- Corrected the earlier R9/R10 cutover claim: React is now wired into the actual Control Plane publish chain.
- Removed source `wwwroot` and duplicate `legacy-ui`; `YazmaBackup.Frontend` is the single UI source.
- Added mandatory `BuildProductionReactFrontend` MSBuild publish target.
- Next.js static export is injected into publish `wwwroot` through `ResolvedFileToPublish`.
- Frontend failure now fails Control Plane publish.
- Production frontend builds fail closed without `package-lock.json` and use `npm ci`.
- Removed misleading legacy frontend rollback source/script.
- Replaced old R6.5-R10 source-string/legacy cutover VERIFY gates with a real build/export/publish-integration gate.
- Added publish artifact fingerprint support in `DEPLOY_FRONTEND.ps1`.
- Agent runtime unchanged.

## R25.0 — DR Execution Controller + PostgreSQL Consistency Fence
- Added persistent DR execution sessions generated from the R24 dependency-safe Recovery DAG.
- Added strict pending -> approved -> verified progression; only current dependency-safe step may advance.
- Added session cancel/completion lifecycle and read/write APIs.
- Closed the R17 active-active completion lease race at the PostgreSQL row-lock boundary.
- Added `completion_lease_id` execution identity and stale/expired lease rejection.
- Fixed command idempotency uniqueness to include `command_type`.
- Added React DR Execution module.
- Does not silently execute destructive restore commands.
- Agent runtime unchanged.

## R24.0 — Business Service Graph + Dependency-Aware Recovery DAG
- Added persistent Business Service dependency records.
- Added backup-admin dependency create/update/delete APIs.
- Added dependency-aware topological Recovery DAG.
- Required dependencies always precede dependent services.
- Added fail-closed dependency-cycle detection; cyclic services are BLOCKED.
- Added React Service Graph / Recovery DAG module.
- Preserved R23 Business Continuity, R22 Recovery Fabric and prior safety gates.
- Does not silently execute restore.
- Agent runtime unchanged.

## R23.0 — Business Continuity Command Center
- Added business-service continuity view derived from explicit Backup Policy naming.
- Added evidence-based criticality using configured RTO and recovery scope.
- Added Continuity Score and critical-service readiness counters.
- Added recovery coverage-gap detection.
- Added deterministic non-executing Disaster Simulation recovery order.
- Added read-only `/api/v1/admin/business-continuity`.
- Added large-card React Business Continuity module.
- Does not invent business ownership/dependencies or silently trigger restore.
- Preserved R22 Recovery Fabric, R21 Closed-Loop Protection and PostgreSQL transaction engines.
- Agent runtime unchanged.

## R22.0 — Enterprise Recovery Fabric
- Added fleet-wide disaster recovery readiness score.
- Added per-Recovery-Plan readiness scoring from real recovery evidence.
- Added measured RTO versus configured RTO target.
- Added recovery evidence freshness and missing-policy coverage checks.
- Added RTO breach and plans-without-evidence counters.
- Added read-only `/api/v1/admin/recovery-fabric`.
- Added large-card React Recovery Fabric module.
- Does not fabricate restore evidence or silently execute recovery.
- Preserved R21 Closed-Loop Protection, R20 Predictive Protection, R17 atomic completion and R16 command transaction engine.
- Agent runtime unchanged.

## R21.0 — Closed-Loop Protection Orchestrator
- Connected R20 Predictive Protection risks to the existing Autonomous Remediation lifecycle.
- Added closed-loop case states: detected, diagnosing, awaiting-approval, applying, verifying, verified and needs-attention.
- Added safe one-click `diagnose-only` initiation for eligible SLA/restore-evidence cases.
- Resolves diagnosis target from trusted Backup Policy data instead of accepting arbitrary paths.
- Suppresses duplicate active diagnosis runs for the same Agent/repository.
- Preserves security-admin approval for mutating remediation.
- Surfaces post-remediation verification state in the React console.
- Preserved R20 Predictive Protection, R19 Fleet Autopilot, R17 atomic completion and R16 command transaction engine.
- Agent runtime unchanged.

## R20.0 — Predictive Protection Engine
- Added pre-overdue backup SLA risk prediction.
- Added measured repository capacity exhaustion-date prediction.
- Added restore-evidence aging early warning.
- Added Agent stability early warning.
- Added per-prediction confidence percentage and expected threshold time.
- Added read-only `/api/v1/admin/predictive-protection`.
- Added React Predictive Protection module.
- Preserved R19 Autopilot, R18 Intelligence, R17 atomic completion and R16 command transaction engine.
- Agent runtime unchanged.

## R19.0 — Fleet Autopilot + Restore Readiness
- Added Restore Readiness score using 30-day recovery evidence and evidence freshness.
- Added SLA risk aggregation across failed backup, overdue policy and repository capacity.
- Added Fleet Autopilot solution-plan classification.
- Separates safe auto-diagnosis, approval-required changes and operator planning.
- Added read-only `/api/v1/admin/fleet-autopilot`.
- Added large-card React Fleet Autopilot module.
- Preserved R18 intelligence, R17 atomic completion and R16 command transaction engine.
- Agent runtime unchanged.

## R18.0 — Enterprise Backup Intelligence
- Added fleet-wide backup health scoring.
- Added prioritized risk center with plain-language recommendations.
- Correlates offline Agents, overdue policies, failed backups, protection locks and repository capacity.
- Added NAS capacity warning/critical forecasting.
- Added read-only `/api/v1/admin/fleet-intelligence`.
- Added large-card React Backup Intelligence module.
- Preserved approval-gated autonomous mutation safety.
- Agent runtime unchanged.

## R17.0 — Multi-Aggregate Atomic Completion
- Command completion moved to a PostgreSQL Serializable multi-aggregate transaction.
- Exact command row is locked before completion commit.
- Existing resilience side effects are preserved before persistence.
- Post-resilience authoritative state is projected to normalized tables inside the same transaction.
- Partial command/recovery/repository/alarm state is prevented by atomic commit/rollback.
- R16 command lease engine and all prior rollback paths preserved.
- Agent runtime unchanged.

## R16.0 — PostgreSQL Command Transaction Engine
- Command claim ownership moved to PostgreSQL `FOR UPDATE SKIP LOCKED`.
- Added durable lease ID, lease expiry and last-renewal columns.
- Added claimable-command and lease-expiry indexes.
- Added atomic database lease renewal.
- Added expired-lease retry and max-attempt exhaustion under the database fence.
- Added command retention pruning to the command transaction.
- Preserved multi-aggregate completion/resilience on the full transactional projection path.
- Preserved R15 focused mutations, R14 direct reads and R12 state fencing.
- Agent runtime unchanged.

## R15.0 — Direct SQL Mutation Core Cutover
- Added focused Serializable PostgreSQL mutation transactions.
- Command enqueue now updates only the command row plus compatibility state CAS.
- Policy create/enable/delete now update only the affected policy row plus compatibility state CAS.
- Added `YAZMABACKUP_DIRECT_SQL_MUTATIONS=false` emergency rollback switch.
- Preserved R14 direct SQL reads, R13 normalized schema and R12 conflict fencing.
- Agent runtime unchanged.

## R14.0 — Direct SQL Read Cutover
- Routed Agents, Commands, Policies, Audit, Alarms and operational metrics directly to normalized PostgreSQL queries.
- Removed high-volume read dependency on in-memory StateDocument when PostgreSQL is active.
- Added rollback switch `YAZMABACKUP_DIRECT_SQL_READS=false`.
- Added direct read-path visibility in state-engine health and HA/DR UI.
- Preserved R13 normalized transactional projection and R12 CAS/fencing.
- Agent runtime unchanged.

## R13.0 — Normalized Enterprise PostgreSQL Schema
- Added normalized PostgreSQL hot aggregates for Agents, Commands, Backup Policies, Audit Events and Alarms.
- Added transactional projection in the same commit as the R12 authoritative state row.
- Added schema migration registry and version 13.0.
- Added FK, idempotency, pending-command, due-policy, audit correlation/BRIN and alarm indexes.
- Added normalized projection counts to state-engine health and React HA/DR UI.
- Added direct PostgreSQL schema validation script without exposing database passwords on command arguments.
- Preserved R12 transactional CAS/fencing, R10.5 DR, R10.4 orchestration, R6.8 AutoKey and R6.9 Global NAS.
- Agent runtime unchanged.

## R12.0 — Transactional PostgreSQL State Engine + Production Active/Active
- Production active-active now requires PostgreSQL; file-state active-active fails closed.
- Added Npgsql-backed authoritative Control Plane state.
- Added serializable atomic version compare-and-swap commits and stale-node fencing.
- Added idempotent file-state → PostgreSQL first-start bootstrap.
- Added state-engine health/status API and React visibility.
- Added PostgreSQL active-active configuration/validation scripts.
- Added CMS-encrypted pg_dump DR snapshot without passing DB connection secrets on process arguments.
- Preserved R10.5 security-state DR and R11 conflict semantics.
- Agent runtime unchanged.

## R11.0 — Distributed State Engine + Active/Active Control Plane
- Added distributed shared-state writer lease and state-version fencing.
- Added optimistic stale-node conflict detection; conflicting mutation returns HTTP 409 + Retry-After.
- Added `active-active` HA role and dual-node readiness tooling.
- Preserved certificate-protected shared Data Protection requirement.
- Added R11 active-active configuration and validation scripts.
- Kept Agent protocol/runtime unchanged.
- Explicitly does not claim the state document is already a relational distributed database.

## R10.5 — Enterprise HA + Zero-Downtime Upgrade Foundation + Disaster Recovery
- Added active/standby fail-closed Control Plane role model.
- Added certificate-protected shared Data Protection requirement for HA.
- Standby blocks mutation API traffic and suppresses background mutators.
- Added live/readiness health, HA status, drain/undrain and DR inventory APIs.
- Added React HA & DR module.
- Added CMS certificate-encrypted DR snapshot with SHA-256 manifest and offline restore validation/safety copy.
- Added controlled drain-based HA switchover runbook.
- Explicitly remains single-writer active/standby; no unsafe active/active claim.
- Agent runtime unchanged.

## R10.4 — Autonomous Remediation Orchestrator + Canary Rollout Controller
- Added persistent DataProtection-backed remediation and rollout state.
- Added cluster-leased remediation orchestration: diagnosis → approval → apply → verification.
- Added deterministic Canary Rollout Controller with preflight, canary staging/apply/observation and health-gated fleet waves.
- Added operator HOLD / RESUME / CANCEL and deterministic idempotency keys.
- Fixed autonomous reliability classification so pending BackupPath commands are not counted as failed backups.
- Preserved fail-closed rollback semantics: installer health rollback is retained; no unsafe central downgrade command was introduced.
- Agent runtime unchanged.

## R10.3 — Autonomous Reliability & Self-Healing
- Added autonomous failure classification and reliability decision endpoint.
- Added Canary/Rollout Guard based on critical incidents, backup success and Agent availability.
- Added React Autonomous Ops queue with diagnosis and allowlisted self-healing.
- Silent mutation remains disabled; mutation requires operator approval.
- Agent runtime unchanged.

## R10.2 — Production Reliability & Observability
- Added live Reliability/SLO/Diagnostic Center across Control Plane, Agent connectivity, backup, repository, MeshCentral, alarms and cluster state.
- Added response correlation ID and Server-Timing headers.
- Added correlation-aware API errors and 30-second frontend API timeout.
- Added production smoke-test script and dedicated reliability VERIFY gate.
- Agent runtime unchanged.

## R10.1 — Enterprise Hardening
- Global error/loading boundaries, skeletons, toast infrastructure and Ctrl+K command palette.
- Accessibility hardening: skip-link, focus-visible, reduced-motion, increased contrast.
- Deterministic npm ci, production dependency audit, export sanity and 25 MB budget gates.
- Backend and Agent unchanged.

## R10.0 — Full React Production Cutover
- Frontend migration reaches 100% primary production cutover.
- Restore Explorer + command polling + granular restore.
- Pilot Readiness / Deep Validation.
- Management API token + break-glass recovery-code management.
- Notification delivery history.
- Legacy UI no longer published under wwwroot; source-only rollback retained.
- Backend and Agent unchanged.

## R9.0 — React Platform Cutover Candidate
- Large React migration: policy CRUD, backup-history detail, alarm workflow/bulk actions, Mesh connector configuration, owner editing, Global NAS management, Recovery Runbooks and notification routes.
- Existing restore, exact Mesh NodeId deployment, RBAC user management and staged Agent lifecycle remain integrated.
- Frontend migration indicator raised to 97%.
- Legacy UI retained only as rollback fallback pending Windows production build/field validation.
- Control Plane backend and Agent runtime unchanged.

## R8.3 — React Deep Operational Migration
- Real Restore enqueue workflow moved to React.
- Exact MeshCentral NodeId selection + Agent deployment moved to React.
- Recovery Plan creation moved to React.
- Management user/RBAC creation moved to React.
- Signed Agent stage/apply update controls moved to React with confirmation.
- Backend and Agent runtime unchanged.

## R8.2 — React Operational Workflows
- High-value operational workflows moved from legacy/read-only shells to React using existing APIs.
- Backend and Agent unchanged.

## R8.1 — Full React Module Migration
- Expanded Next.js/React shell to Operations, Validation, Recovery, Alarms, Lifecycle, MeshCentral, Users, Security and Audit.
- Added live API-driven enterprise tables/cards instead of demo telemetry.
- Preserved .NET 10 backend, Agent protocol and all known-good runtime fixes.
- Legacy fallback remains available while deep operational forms finish migration.

## R8.0 — Next.js Enterprise Frontend Migration
- Added TypeScript + React + Next.js App Router frontend.
- Added Tailwind CSS 4, source-owned shadcn-style primitives, Tremor and Lucide.
- Migrated login, dashboard, computer fleet, policies, global NAS summary and settings export/import.
- Uses existing .NET 10 APIs; no backend split and no Agent protocol change.
- Added deterministic frontend build/deploy/rollback scripts.
- Preserved R7.2 legacy UI as non-destructive fallback.

## R7.2 — Tailwind + Flowbite-Compatible Enterprise UI
- Added locally generated Tailwind CSS v4 utilities.
- Added Flowbite-compatible cards, buttons, inputs, badges, sidebar, tables and stat components.
- Preserved current functional DOM IDs, APIs and backend.
- Added reproducible CSS build scaffold.
- UI-only; Agent unchanged.

## R7.1 — Luminous Enterprise UI
- Major premium visual uplift across the Control Plane.
- Added aurora light background, glass shell, luminous navigation, elevated cards and premium micro-interactions.
- Refined Agent cards, metrics, tables, wizards, forms and settings transfer surfaces.
- Added reduced-motion accessibility support.
- UI-only; Agent runtime unchanged.

## R7.0 — Encrypted Settings Export / Import
- Added password-protected `.ybsettings` export/import.
- Export includes global NAS profile, computer owner metadata and backup policies.
- Package uses PBKDF2-SHA256 + AES-256-GCM.
- Import maps computers by machine name and skips duplicate policies.
- Restored NAS profile is automatically applied to all current Agents.
- Agent runtime unchanged.

## R6.9 — Persistent Global NAS Profile
- NAS repository path, repository ID and username are now server-side persistent.
- NAS password is protected with Data Protection and never returned to the browser.
- Leaving password blank preserves the current password.
- Global profile changes are automatically pushed to all Agents.
- New Agents receive the current profile automatically after authenticated heartbeat.
- Versioned idempotency prevents unnecessary reprovision when settings have not changed.
- Agent runtime unchanged.

## R6.8 — Repository Encryption Key Auto-Provision
- Fixed `Şifreleme Anahtarı` failures when NAS credential tests pass.
- Policy scheduler now pre-provisions repository encryption keys before BackupPath.
- Added one-shot auto-heal for legacy/existing policy failures caused by a missing Agent repository key ring.
- Preserved RSA-OAEP-SHA256 wrapping and Agent DPAPI storage.
- No Agent runtime change.

## R6.7.1 — VERIFY 30-Minute Reconciliation Gate Fix
- Fixed false failure at `[12p/23]`.
- Replaced incorrectly escaped R6.7 regex source checks with literal `Contains()` checks.
- R6.7 runtime behavior remains unchanged.

## R6.7 — MeshCentral Heartbeat-Aware Deployment Timeout
- Fixed false `failed` deployments exactly five minutes after MeshCentral dispatch.
- 5-minute meshctrl reply timeout now transitions to heartbeat reconciliation.
- Exact DeploymentId/NodeId/AgentId binding remains authoritative.
- 30-minute reconciliation remains the terminal deadline.
- Stale dispatching after worker restart no longer immediately fails or re-dispatches.
- Mesh UI now shows `Agent Kaydı Bekleniyor`.
- Agent runtime unchanged.

## R6.6.2 — VERIFY Operation Card Gate Fix
- Fixed `[12l/23]` invalid regular-expression failure on Windows PowerShell.
- Replaced dynamic regex matching with literal `Contains()` checks for operation-card module keys.
- Runtime/UI/Agent behavior unchanged.

## R6.6.1 — VERIFY UTF-8 / XL Card Gate Fix
- Fixed Windows PowerShell 5.1 false failure at `[12k/23]`.
- VERIFY now reads Control Plane HTML/CSS/JS explicitly as UTF-8.
- R6.2/R6.5 UI gates validate structural selectors/tokens instead of Unicode comment text.
- Runtime and UI behavior unchanged.

## R6.6 — Modern .NET 10 / C# 14 Template Baseline
- Added modern `YazmaBackup.slnx` solution format while retaining `.sln`.
- Standardized repository C# 14 coding conventions through `.editorconfig`.
- Explicitly enabled built-in .NET analyzers.
- Added template-baseline verification script.
- Kept stable .NET 10 / C# 14 pinning and prerelease SDKs disabled.
- No runtime/Agent behavior changed.

## R6.5 — Ultra Professional Enterprise UI
- Refined the entire Control Plane into a premium light enterprise design system.
- Reworked shell, navigation, typography, card depth, forms, tables, badges and guided wizards.
- Enhanced device cards and operation-card hierarchy.
- Added subtle page/card motion without changing functionality.
- UI-only; Agent binary unchanged.

## R6.4 — Guided Backup & Restore Wizards
- Added 5-step Backup wizard.
- Added 5-step Restore wizard.
- Wizards guide users to existing real controls.
- Added review step and responsive wizard navigation.
- UI-only; Agent binary unchanged.

## R6.3 — Every Module Gets Separate XL Operation Cards
- Added task-oriented operation-card maps to every major module.
- Cards navigate only to real existing controls.
- Existing functional panels are promoted as independent XL operation surfaces.
- Added focus animation and responsive one-card-per-row behavior.
- UI-only; Agent binary unchanged.

## R6.2 — All Modules XL Card Experience
- Converted the entire Control Plane visual hierarchy to large, readable cards.
- Enlarged Computers cards and preserved inline owner editing/actions.
- Promoted forms, operations, policies, restore, validation, recovery, storage, alarms, MeshCentral, users, security, audit and settings surfaces to XL cards.
- Added responsive one-card-per-row behavior.
- UI-only revision; R6.1 Agent runtime remains unchanged.

## R6.1 — Zero-Touch Agent Lifecycle + Big Cards
- Converged on supplied R6 Big Cards baseline.
- Added staged in-place Agent apply command.
- Preserves AgentId/token/config/repository keys/NAS credentials.
- Backup policies remain server-side across upgrades.
- Existing installer rollback retained.
- Added XL Agent Lifecycle management surface.

## R5.19.8 — Domainless Computer Ownership
- Added editable per-computer Kullanıcı / Sahibi metadata.
- Added persistent AssignedUser API/store support.
- Added owner-aware search, selectors and Asset 360 display.
- Existing machine identity and heartbeat behavior preserved.

## R5.19.7 — Automatic Repository Encryption Key Bootstrap
- Fixed `Repository key ring is not provisioned` backup failures.
- Added DataProtection-protected central repository key vault.
- Added automatic RSA-OAEP repository-key provisioning before every backup.
- Same repository key is reused across Agent reinstalls and compatible endpoints.
- Added VERIFY [12g/23] key-vault and ordering invariants.

## R5.19.6 — Backup Diagnostics + 503 Resilience
- Added end-to-end backup failure diagnostics.
- Added source/repository/error context to Agent logs and Backup History UI.
- Added failure categorization for NAS/SMB, ACL, repository key, VSS, filesystem and protection errors.
- Added bounded exponential backoff for HTTP 502/503/504.
- No security gate was weakened.

## R5.19.5 — Deterministic NAS Save + Test
- Added one-step secure NAS credential provision + real SMB test.
- Credential is DPAPI-persisted only after successful live SMB read/write validation.
- Added Windows error code and share-root diagnostics to UI.
- Kept separate “Kayıtlı Kimlikle Tekrar Test Et” action for later health checks.

## R5.19.4.2 — NAS Analyzer Build Fix
- Fixed CA1416 by marking NasCredentialStore as Windows-only.
- Fixed CA1822 by making NasCredentialStore.Exists static.
- No runtime behavior or security model change.

## R5.19.4.1 — VERIFY NAS Invariant Fix
- Fixed false-positive [12e/23] NAS credential invariant.
- VERIFY now validates the actual AgentPaths-based NAS credential architecture.
- Runtime behavior unchanged.

## R5.19.4 — Secure NAS Credentials + Real Test
- Added NAS username/password provisioning from the UI.
- Added RSA-OAEP-SHA256 transport and Agent-side DPAPI storage.
- Added endpoint-side SMB read/write connection test.
- Configured SMB credentials are automatically used for repository operations.
- Password is never persisted in browser storage.

## R5.19.3 — UI Readability + NAS Settings
- Fixed overlapping text in Settings quick-start cards.
- Added NAS & Repository configuration workspace.
- Added safe default propagation to Policy, Pilot and Manual Backup forms.
- No credential storage and no backend contract change.

## R5.19.2 — CA1859 Release Build Fix
- Fixed Agent Release build failure caused by CA1859 warnings-as-errors.
- `BuildRestoreEntries` now exposes its concrete `RestoreEntryDto[]` return type.
- No behavior or API contract change.

## R5.19.1 — SAFE DOM
- Removed the two Restore Explorer `innerHTML` assignments rejected by VERIFY [9/23].
- Empty states now use `createElement` + `textContent`.
- Security verification policy remains unchanged.

## R5.19 — Functional Command Center + Runtime Convergence
- Real backup progress/stage/ETA telemetry.
- Manifest-backed Restore Explorer tree.
- Functional Asset 360 tabs.
- Alarm SLA/owner/note/filter/bulk workflow.
- Restored exact MeshCentral deployment binding, Agent auth self-heal and Windows-safe traversal.

## R5.18 — Live Data Command Center
- Asset 360 is now driven by real device, policy, repository and backup data.
- Live Backup Ops consumes real transfer telemetry.
- Restore Explorer consumes real restore points.
- Capacity Forecast consumes repository health/capacity data.
- Alarm Triage consumes real alarm severity/status data.
- No new privileged API or storage schema introduced.

## R5.17 UI — Command Center Experience
- Added Asset 360 device-detail workspace.
- Added live backup operation lanes.
- Added Restore Explorer file-manager experience.
- Added NAS capacity forecast surface.
- Added Alarm Center P1/P2/P3 triage board.
- Runtime and API contracts unchanged.

## R5.16 UI — Premium Operations Experience
- Computers: Asset 360-style device operations workspace.
- Backups: process-driven Operations Center.
- NAS & Storage: Capacity & Health Console.
- Restore: premium three-stage wizard experience with safety guidance.
- Existing UI IDs/data contracts and backend behavior preserved.

## R5.15 UI — All Modules Enterprise XL
- Converted every main YazmaBackup module to the large-card Enterprise Light XL visual system.
- Added module hero surfaces, extra-large tables/forms, restore wizard, pilot center, storage/alarm metrics,
  MeshCentral workspace, RBAC/security panels, audit surface and settings guidance cards.
- Preserved JavaScript IDs/data attributes and server/runtime behavior.

## R5.14 UI — Enterprise Light XL
- Rebuilt the visual language around extra-large module cards and extra-large sidebar menu cards.
- Introduced premium corporate blue light-theme hierarchy, soft depth, micro-motion and responsive polish.
- Preserved all existing element IDs/data attributes and runtime behavior.


## R5.5 - Bootstrap-Safe ACL Recovery
- MeshCentral remote bootstrap no longer creates `C:\ProgramData\YazmaBackup\Logs` before the installer repairs ProgramData ACLs.
- Installer output starts in the isolated deployment workspace and is copied to the canonical log only after service/enrollment validation.
- Failure evidence is preserved best-effort under `C:\ProgramData\YazmaBackupDeployLogs` even when the legacy YazmaBackup tree is inaccessible.
- A stale/denied `Logs` directory can no longer block ACL self-healing.
# YazmaBackup v1.2.0 R1

## Büyük geçiş: MeshCentral Fleet Connector

- State schema 11 ve PostgreSQL migration 008 eklendi.
- MeshCentral connector credential ASP.NET Data Protection ile korunuyor.
- `ServerInfo`, `ListDevices` ve `RunCommand` tabanlı connector servisi eklendi.
- Fleet inventory ve hostname tabanlı güvenli Agent eşleştirmesi eklendi.
- Belirsiz eşleşmeler otomatik bağlanmıyor.
- Varsayılan 5 dakikalık otomatik MeshCentral senkronizasyon servisi eklendi.
- Fleet yönetim UI'ı ve bağlantı/senkronizasyon/deployment API'leri eklendi.
- Tek kullanımlık `ybboot_` ve `ybpkg_` dağıtım biletleri eklendi.
- Agent ZIP SHA-256 doğrulaması zorunlu hale getirildi.
- Deployment kanıtı heartbeat eşleşmesiyle `succeeded`, 30 dakikalık kanıt yokluğuyla `failed` durumuna bağlandı.
- Eski manuel MeshCentral Node link/status ingestion API'leri geriye dönük uyumluluk için korundu.
## R2 - Analyzer hardening
- CA1848 giderildi: MeshCentral connection test ve scheduled sync logları `LoggerMessage.Define` ile allocation-conscious logging kullanıyor.
- CA1859 giderildi: `ParseDevices` gerçek dönüş türü olan `MeshCentralInventoryDeviceRecord[]` olarak daraltıldı.
- Analyzer bastırma, `NoWarn` veya warnings-as-errors gevşetmesi yapılmadı.
## R3 - Gerçek MeshCentral saha sözleşmesi
- `https://mesh.gursoyoto.com.tr` kullanıcı girdisi `meshctrl` için otomatik `wss://mesh.gursoyoto.com.tr` adresine çevriliyor.
- `ServerInfo` artık MeshCentral'ın gerçek düz metin çıktısını kabul ediyor; JSON zorunluluğu kaldırıldı.
- `ListDevices --json` gerçek fleet envanteri için JSON olarak kalıyor.
- Kimlik doğrulama gerçek CLI sözleşmesine uyarlandı: `--loginuser` + parola modunda `--loginpass`; secret OS proses komut satırına konmuyor, Node bridge environment üzerinden process.argv içine aktarılıyor.
- Fleet eşleştirme önceliği `hostname/rname -> Agent MachineName`, yalnız eşleşme yoksa display `name` fallback olacak şekilde güvenli hale getirildi.
- Gerçek saha kanıtı: ServerInfo ve ListDevices başarılı; ilk tek cihaz Agent deployment/heartbeat doğrulaması sıradaki kapı.

## R4 - MeshCentral deployment reliability / 504 elimination
- Gerçek saha testiyle doğrulanan `meshctrl RunCommand --run` sözleşmesi kalıcılaştırıldı; eski `--cmd` kullanımı kalite kapısıyla yasaklandı.
- Agent dağıtım HTTP isteği artık MeshCentral komutunu beklemiyor; `202 Accepted` ile hemen dönüp işi arka plan worker'ına bırakıyor.
- `queued` işler restart sonrası yeniden keşfedilip işleniyor; durumlar `queued -> dispatching -> installing -> succeeded/failed` olarak kanıta bağlı ilerliyor.
- Uzak kurulum `YAZMABACKUP_DEPLOYMENT_SUCCESS` / `YAZMABACKUP_DEPLOYMENT_ERROR` kanıtlarıyla doğrulanıyor; meshctrl bazı CLI hatalarını exit-code 0 döndürse bile bilinen hata çıktıları başarısız sayılıyor.
- MeshCentral deployment subprocess'i 5 dakikalık sınırla korunuyor; timeout gerçek `failed` detayı üretiyor.
- `RUN_SERVER.ps1` state yolu Control Plane content-root `.local` yolu ile hizalandı.
- VERIFY teknik-borç taraması oluşturulan `bin/obj/dist/artifacts/publish` dizinlerini dışlıyor; tekrarlı VERIFY self-fail sorunu kapatıldı.
- Windows üzerinde ASP.NET Data Protection anahtarları DPAPI ile diskte korunuyor.


## R5 - MeshCentral installer reliability / field-error closure
- Remote installer now always launches `INSTALL_AGENT.ps1` with `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass`; endpoint execution policy is not modified persistently.
- Child installer exit code is mandatory evidence. A non-zero exit can no longer be misreported as `Remote installer completed`.
- Every MeshCentral install keeps a persistent log under `C:\ProgramData\YazmaBackup\Logs\meshcentral-install-<deployment-id>.log`.
- Remote success requires `YazmaBackupAgent` to be `Running` and both `agent.json` plus `agent-access-token.dpapi` enrollment evidence to appear within 90 seconds.
- Installer service startup now includes a five-second stability window and writes SCM/Event Log/Agent-log diagnostics on failure before rollback.
- `RUN_SERVER.ps1` automatically discovers the newest `dist\YazmaBackupAgent_*_win-x64.zip` when the environment variable is absent or stale.
- VERIFY includes regression gates for execution-policy bypass, child exit propagation, persistent diagnostics, service/enrollment evidence and Agent ZIP auto-discovery.

## R5.1 - Windows service creation reliability / sc.exe closure
- LocalSystem servis oluşturma `sc.exe create` yerine `New-Service` ile yapılır.
- 5 denemeli create retry, açık `DelayedAutoStart=1`, strict sc.exe output/exit-code propagation ve SCM/ImagePath doğrulaması eklendi.
- Installer output UTF-8 sertleştirildi; R5 reliability katmanı korunur.
- Ayrıntı: `VALIDATION_R5.1.md`.

## R5.2
- Uzaktan klasör tarayıcısına kalıcı çoklu seçim, checkbox, tümünü seç, seçim sayacı ve kısmi seçim UX eklendi.
- Çoklu politika/anlık yedek kaynakları mevcut güvenli tek-source backend modeline ayrı işler olarak fan-out edilir.


## R5.3 - ProgramData ACL / Agent identity reliability
- `C:\ProgramData\YazmaBackup` için kurulum öncesi deterministik ACL self-heal eklendi.
- Önceki yarım/bozuk kurulumlardan kalan owner, inheritance ve explicit ACL sapmaları recursive olarak resetlenir.
- Nihai izin modeli yalnız `SYSTEM` ve yerel `Administrators` için Full Control olacak şekilde tekrar uygulanır; owner `SYSTEM` olarak doğrulanır.
- Agent bootstrap başlamadan gerçek dosya yazma probe'u zorunludur; başarısız ACL artık `agent.json Access denied` aşamasına kadar ilerlemez.
- Enrollment token mevcutken `agent.json` / `agent-access-token.dpapi` çiftinden yalnız biri kalmışsa iki stale kanıt birlikte temizlenip güvenli yeniden enrollment uygulanır.
- R5.2 çoklu klasör seçimi ve R5.1 servis reliability katmanları korunur.

## R5.4 - Root-first ACL recovery / Access Denied closure
- ProgramData ACL onarımı recursive-first yerine root-first çalışır; kök erişimi düzeltilmeden `/T` uygulanmaz.
- `C:\ProgramData\YazmaBackup\*` wildcard yaklaşımı tamamen kaldırıldı.
- Native `takeown.exe` / `icacls.exe` çağrıları stdout/stderr + gerçek exit code ile değerlendirilir; recursive recovery tek stale/locked child nedeniyle erken kesilmez.
- Recursive recovery sonrasında kök ACL tekrar SYSTEM + Administrators Full Control ve SYSTEM owner olacak şekilde reassert edilir.
- `agent.json`, `agent-access-token.dpapi` ve `update-public-key.pem` ayrı ayrı sahiplik/ACL normalizasyonundan geçer.
- Bootstrap öncesi create/write/read/delete round-trip preflight zorunludur; başarısızlıkta tüm ACL aşamalarının exit-code tanısı UI'ya taşınır.
- R5.3 saha hatası `icacls ...\*: Erişim engellendi` için kalıcı regresyon kapısı eklendi.

### R5.6 — Native ACL Recovery Determinism
- Fixed Windows PowerShell 5.1 `NativeCommandError` promotion that could abort `icacls.exe /T /C` recovery even when the operation was intentionally best-effort.
- ACL native commands now run through redirected `System.Diagnostics.ProcessStartInfo`; stdout/stderr are plain diagnostic text and exit-code policy is enforced by YazmaBackup.
- Root-first ACL repair, critical identity repair, R5.5 bootstrap-safe logging, and mandatory create/write/read/delete preflight are preserved.


## R5.7 — Windows Service Configuration Determinism
- Eliminated `sc.exe config` from existing Agent service updates after field-observed ExitCode 1639.
- Added typed `Win32_Service.Change` for ImagePath/start mode/account updates.
- Added `Win32_Service.Create` for passwordless gMSA creation; LocalSystem creation remains `New-Service`.
- Rollback/delete no longer depend on `sc.exe config/delete`.
- Service Description is written through the service registry key; recovery actions use deterministic R5.6 native process capture.
- Added VERIFY gates preventing reintroduction of `sc.exe config/create/delete` into install/update/rollback.


## R5.8 — Heartbeat-Driven Deployment Reconciliation
- Agent heartbeat artık unique MeshCentral hostname/name eşleşmesinde bekleyen deployment kaydını event-driven olarak `succeeded` yapar.
- Başarı için normal MeshCentral inventory sync periyodunu bekleme kaldırıldı.
- `installing` kayıtlar varken deployment worker yaklaşık 10 saniyede bir sync fallback yapar; restart sonrası reconciliation da korunur.
- Duplicate hostname durumunda otomatik bağlama yapılmaz; ambiguity-safe davranış korunur.
