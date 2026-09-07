$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
$css=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\app\globals.css')
foreach($x in @('ExecutivePage','Executive Protection Brief','Board-Level Protection Status','Management Attention','yb-density-compact','PanelLeftClose','Rows3')){
 if(-not $ui.Contains($x)){throw ('R27.3 UI invariant eksik: '+$x)}
}
foreach($x in @('R27.3 Ultimate Enterprise Experience','prefers-reduced-motion','yb-premium-card','yb-executive-hero')){
 if(-not $css.Contains($x)){throw ('R27.3 design-system invariant eksik: '+$x)}
}
foreach($x in @('CommandCenterPage','FleetGalaxyPage','RecoveryEvidenceTimeline','Disaster Recovery War Room')){
 if(-not $ui.Contains($x)){throw ('R27 önceki deneyim regresyonu: '+$x)}
}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\wwwroot')){throw 'source wwwroot geri gelmiş.'}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\legacy-ui')){throw 'legacy-ui geri gelmiş.'}
Write-Host 'PASS: R27.3 Ultimate Enterprise Experience statik kalite kapısı.'
