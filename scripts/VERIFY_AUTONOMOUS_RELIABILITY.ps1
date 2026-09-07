$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$console=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('/autonomous-reliability','canaryReady','silentMutation = false','approvalRequiredForMutation = true','reset-repository-circuit')){if(-not $program.Contains($x)){throw ('R10.3 backend autonomy invariant eksik: '+$x)}}
foreach($x in @('AutonomousReliabilityPage','Failure Classification Queue','Rollout Guard','Remediation State Machine','reset-repository-circuit','Preflight + Canary Başlat','Health Gate Sonrası Resume')){if(-not $console.Contains($x)){throw ('R10.3 React autonomy invariant eksik: '+$x)}}
Write-Host 'PASS: R10.3 Autonomous Reliability & Self-Healing statik kalite kapısı.'
