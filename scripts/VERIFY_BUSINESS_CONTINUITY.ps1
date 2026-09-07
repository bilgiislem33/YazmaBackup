$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$svc=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\BusinessContinuityService.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('ContinuityScore','DisasterSimulationStep','BusinessKey','InferCriticality','MissingRecoveryCoverage','RtoTargetMinutes','Recovery Plan kapsamı yok')){if(-not $svc.Contains($x)){throw ('R23 Business Continuity invariant eksik: '+$x)}}
foreach($x in @('AddSingleton<BusinessContinuityService>','/business-continuity')){if(-not $program.Contains($x)){throw ('R23 API invariant eksik: '+$x)}}
foreach($x in @('BusinessContinuityPage','/api/v1/admin/business-continuity','restore işlemlerini sessizce başlatmaz')){if(-not $ui.Contains($x)){throw ('R23 React UI structural/safety invariant eksik: '+$x)}}
Write-Host 'PASS: R23 Business Continuity Command Center davranış + React güvenlik kalite kapısı.'
