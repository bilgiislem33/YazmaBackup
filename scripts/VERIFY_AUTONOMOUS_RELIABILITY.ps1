$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
$console=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('/autonomous-reliability','canaryReady','silentMutation = false','approvalRequiredForMutation = true','reset-repository-circuit')){if(-not $program.Contains($x)){throw ('R10.3 backend autonomy invariant eksik: '+$x)}}
foreach($x in @('AutonomousReliabilityPage','uiText.autonomous.failureQueue','uiText.autonomous.stateMachine','reset-repository-circuit','uiText.autonomous.startCanary','uiText.autonomous.resumeAfterHealth')){if(-not $console.Contains($x)){throw ('R10.3 React autonomy invariant eksik: '+$x)}}
Write-Host 'PASS: R10.3 Autonomous Reliability & Self-Healing statik kalite kapısı.'
