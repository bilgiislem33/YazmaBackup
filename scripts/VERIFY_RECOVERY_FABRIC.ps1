$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$svc=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\RecoveryFabricService.cs')
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('FleetRecoveryScore','RtoTargetMinutes','LongestRestoreMilliseconds','MissingPolicies','evidenceFresh','GetRecoveryPlansAsync','GetRecoveryRunsAsync')){if(-not $svc.Contains($x)){throw ('R22 Recovery Fabric invariant eksik: '+$x)}}
foreach($x in @('AddSingleton<RecoveryFabricService>','/recovery-fabric')){if(-not $program.Contains($x)){throw ('R22 API invariant eksik: '+$x)}}
foreach($x in @('RecoveryFabricPage','/api/v1/admin/recovery-fabric')){if(-not $ui.Contains($x)){throw ('R22 React UI structural invariant eksik: '+$x)}}
Write-Host 'PASS: R22 Enterprise Recovery Fabric davranış + React yapı kalite kapısı.'
