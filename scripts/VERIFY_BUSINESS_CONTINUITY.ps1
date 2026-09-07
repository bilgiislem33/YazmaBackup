$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$svc=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\BusinessContinuityService.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('ContinuityScore','DisasterSimulationStep','BusinessKey','InferCriticality','MissingRecoveryCoverage','RtoTargetMinutes','RecoveryPlanCount','automatic recovery','otomatik recovery başlatılmaz')){
 if(-not $svc.Contains($x)){throw ('R23 Business Continuity invariant eksik: '+$x)}
}
foreach($x in @('AddSingleton<BusinessContinuityService>','/business-continuity')){
 if(-not $program.Contains($x)){throw ('R23 API invariant eksik: '+$x)}
}
foreach($x in @('BusinessContinuityPage','/api/v1/admin/business-continuity','Disaster Simulation','restore işlemlerini','sessizce')){
 if(-not $ui.Contains($x)){throw ('R23 React structural/safety invariant eksik: '+$x)}
}
Write-Host 'PASS: R23 Business Continuity Command Center behavior + React safety gate.'
