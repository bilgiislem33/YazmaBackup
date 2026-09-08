$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
foreach($x in @('CommandCenterPage','YazmaBackup Command Center','NOC Mode','Live Operations','Repository Radar','Live Activity Stream','Gerçek yüzde telemetrisi olmadığı için progress yüzdesi uydurulmaz','Ctrl+K')){
 if(-not $ui.Contains($x)){throw ('R27 frontend experience invariant eksik: '+$x)}
}
foreach($x in @('MapGet("/commands"','MapGet("/repository-health"','MapGet("/alarms/summary"')){
 if(-not $program.Contains($x)){throw ('R27 gerçek telemetri API invariant eksik: '+$x)}
}
if($ui.Contains('R10.0 Full React Cutover')){throw 'R27: yanıltıcı R10 Full React Cutover dashboard banner kalmış.'}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\legacy-ui')){throw 'R27 single UI source ihlali: legacy-ui geri gelmiş.'}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\wwwroot')){throw 'R27 single UI source ihlali: source wwwroot geri gelmiş.'}
Write-Host 'PASS: R27 Frontend Experience · Command Center · NOC · gerçek telemetri statik kalite kapısı.'
