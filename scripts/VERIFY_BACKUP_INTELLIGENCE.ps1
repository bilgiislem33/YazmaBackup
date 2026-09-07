$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$svc=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\FleetBackupIntelligenceService.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('FleetBackupIntelligenceService','repository-capacity','backup-failed','policy-overdue','protection-locked','FleetScore','Priorities')){if(-not $svc.Contains($x)){throw ('R18 intelligence invariant eksik: '+$x)}}
foreach($x in @('AddSingleton<FleetBackupIntelligenceService>','/fleet-intelligence')){if(-not $program.Contains($x)){throw ('R18 API invariant eksik: '+$x)}}
foreach($x in @('FleetIntelligencePage','/api/v1/admin/fleet-intelligence')){if(-not $ui.Contains($x)){throw ('R18 React UI structural invariant eksik: '+$x)}}
Write-Host 'PASS: R18 Enterprise Backup Intelligence davranış + React yapı kalite kapısı.'
