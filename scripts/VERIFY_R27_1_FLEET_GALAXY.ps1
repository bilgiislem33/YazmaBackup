$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('FleetGalaxyPage','Fleet Galaxy','Device 360','Fleet Pulse','Fleet Galaxy konumu ağ topolojisi değildir','Device 360 yalnız mevcut API verisini gösterir')){
 if(-not $ui.Contains($x)){throw ('R27.1 invariant eksik: '+$x)}
}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\wwwroot')){throw 'source wwwroot geri gelmiş.'}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\legacy-ui')){throw 'legacy-ui geri gelmiş.'}
Write-Host 'PASS: R27.1 Fleet Galaxy + Device 360 statik kalite kapısı.'
