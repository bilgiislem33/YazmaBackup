$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
$orchestrator=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\AutonomousRemediationOrchestrator.cs')
$console=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')

foreach($token in @(
  'AutonomousOrchestrationStore',
  'AutonomousRemediationOrchestratorService',
  'autonomous-orchestration.protected',
  'TryAcquireOrRenewClusterLeaseAsync("autonomous-remediation-orchestrator"',
  'queued-diagnosis',
  'awaiting-approval',
  'canary-staging',
  'canary-observing',
  'wave-staging',
  'wave-observing',
  'local-installer-health-rollback'
)){
  if(-not $orchestrator.Contains($token) -and -not $console.Contains($token)){throw ('R10.4 orchestrator invariant eksik: '+$token)}
}

foreach($token in @(
  '/autonomous/remediations',
  '/autonomous/rollouts',
  '/autonomous/rollouts/{rolloutId:guid}/start',
  '/autonomous/rollouts/{rolloutId:guid}/hold',
  '/autonomous/rollouts/{rolloutId:guid}/resume',
  'diagnose-only',
  'CompletedAtUtc is not null'
)){
  if(-not $program.Contains($token)){throw ('R10.4 API/safety invariant eksik: '+$token)}
}

foreach($token in @(
  'Autonomous Remediation & Canary Controller',
  'Remediation State Machine',
  'Failure Classification Queue',
  'Preflight + Canary Başlat',
  'Health Gate Sonrası Resume',
  'Rollback Semantiği'
)){
  if(-not $console.Contains($token)){throw ('R10.4 UI invariant eksik: '+$token)}
}

# Fail-closed safety: mutating remediation is approval-gated and diagnose-only can complete without approval.
foreach($token in @(
  'State = string.Equals(x.ActionId, "diagnose-only", StringComparison.Ordinal) ? "completed" : "awaiting-approval"',
  'awaiting-approval'
)){
  if(-not $orchestrator.Contains($token)){throw ('R10.4 approval-gate invariant eksik: '+$token)}
}
if(-not $program.Contains('diagnose-only')){throw 'R10.4 diagnose-only API invariant eksik.'}

# Fail-closed safety: no automatic downgrade command was introduced.
$domain=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Domain\Models.cs')
if($domain.Contains('RollbackAgentUpdate')){throw 'R10.4 unexpected Agent downgrade command detected.'}

Write-Host 'PASS: R10.4 Autonomous Remediation Orchestrator + Canary Rollout Controller statik kalite kapısı.'
