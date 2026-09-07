$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$svc=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\FleetAutopilotService.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('RestoreReadinessScore','GetRecoveryRunsAsync','SlaRiskCount','auto-diagnose','approval-required','operator-plan')){
 if(-not $svc.Contains($x)){throw ('R19 Autopilot invariant eksik: '+$x)}
}
foreach($x in @('AddSingleton<FleetAutopilotService>','/fleet-autopilot')){
 if(-not $program.Contains($x)){throw ('R19 API invariant eksik: '+$x)}
}
foreach($x in @('Fleet Autopilot','Restore Hazırlık Puanı','Akıllı Çözüm Planı','yönetici onayı')){
 if(-not $ui.Contains($x)){throw ('R19 UI invariant eksik: '+$x)}
}
Write-Host 'PASS: R19 Fleet Autopilot + Restore Readiness statik kalite kapısı.'
