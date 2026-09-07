$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$svc=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\PredictiveProtectionService.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('sla-breach','capacity-predict','restore-evidence','agent-stability','ConfidencePct','EstimatedDaysToFull','RestoreDrillIntervalDays')){
 if(-not $svc.Contains($x)){throw ('R20 predictive invariant eksik: '+$x)}
}
foreach($x in @('AddSingleton<PredictiveProtectionService>','/predictive-protection')){
 if(-not $program.Contains($x)){throw ('R20 API invariant eksik: '+$x)}
}
foreach($x in @('Predictive Protection','Öngörü Sağlık Puanı','Yaklaşan Riskler','Güven %')){
 if(-not $ui.Contains($x)){throw ('R20 UI invariant eksik: '+$x)}
}
Write-Host 'PASS: R20 Predictive Protection Engine statik kalite kapısı.'
