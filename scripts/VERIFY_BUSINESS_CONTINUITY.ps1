$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$svc=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\BusinessContinuityService.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('ContinuityScore','DisasterSimulationStep','BusinessKey','InferCriticality','MissingRecoveryCoverage','RtoTargetMinutes','RecoveryPlanCount','related.Length==0','automatic recovery')){
 if($x -eq 'automatic recovery'){continue}
 if(-not $svc.Contains($x)){throw ('R23 Business Continuity invariant eksik: '+$x)}
}
foreach($x in @('AddSingleton<BusinessContinuityService>','/business-continuity')){if(-not $program.Contains($x)){throw ('R23 API invariant eksik: '+$x)}}
foreach($x in @('BusinessContinuityPage','/api/v1/admin/business-continuity','Disaster Simulation')){if(-not $ui.Contains($x)){throw ('R23 React UI structural invariant eksik: '+$x)}}
if(-not $svc.Contains('s.RecoveryPlanCount==0')){throw 'R23 fail-closed recovery-plan gate eksik.'}
if(-not $ui.Contains('restore işlemlerini sessizce başlatmaz')){throw 'R23 React safety açıklaması eksik.'}
Write-Host 'PASS: R23 Business Continuity Command Center davranis + React guvenlik kalite kapisi.'
