$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
$strings=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\lib\ui-strings.ts')
foreach($x in @('FleetGalaxyPage','uiText.fleet.liveMap','uiText.fleet.device360','FleetPulse','Cihaz 360 yalnız mevcut API verisini gösterir')){
 if(-not $ui.Contains($x)){throw ('R27.1 invariant eksik: '+$x)}
}
foreach($x in @('Canlı Filo Görünümü','Cihaz 360')){if(-not $strings.Contains($x)){throw ('R29.2 merkezi Türkçe filo metni eksik: '+$x)}}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\wwwroot')){throw 'source wwwroot geri gelmiş.'}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\legacy-ui')){throw 'legacy-ui geri gelmiş.'}
Write-Host 'PASS: R27.1 Filo Görünümü + Cihaz 360 statik kalite kapısı.'
