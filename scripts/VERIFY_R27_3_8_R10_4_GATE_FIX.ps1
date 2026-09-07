$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$gate=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\VERIFY_AUTONOMOUS_ORCHESTRATOR.ps1')
$orch=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\AutonomousRemediationOrchestrator.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')

if($gate.Contains("'silent mutation'")){throw 'Eski R10.4 wording gate geri gelmiş.'}
foreach($x in @(
 'awaiting-approval',
 'diagnose-only',
 'canary-staging',
 'canary-observing',
 'wave-staging',
 'wave-observing',
 'local-installer-health-rollback'
)){
 if(-not $orch.Contains($x)){throw ('R10.4 orchestration behavior invariant eksik: '+$x)}
}
foreach($x in @(
 'Autonomous Remediation & Canary Controller',
 'Remediation State Machine',
 'Failure Classification Queue',
 'Preflight + Canary Başlat',
 'Health Gate Sonrası Resume',
 'Rollback Semantiği'
)){
 if(-not $ui.Contains($x)){throw ('R10.4 current React UI invariant eksik: '+$x)}
}
foreach($x in @(
 '/autonomous/remediations',
 '/autonomous/rollouts',
 'diagnose-only',
 'CompletedAtUtc is not null'
)){
 if(-not $program.Contains($x)){throw ('R10.4 current API/safety invariant eksik: '+$x)}
}
Write-Host 'PASS: R27.3.8 R10.4 behavior-based gate fix.'
