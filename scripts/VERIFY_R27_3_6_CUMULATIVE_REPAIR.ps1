$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$verify=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\VERIFY.ps1')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
if($verify.Contains("'meshcentral/connector','meshcentral/test'")){throw 'Eski Mesh literal VERIFY gate geri gelmiş.'}
foreach($x in @('PilotCenterPage','function Metric','Building2','Network','description?:string','function bytes(')){if(-not $ui.Contains($x)){throw ('Cumulative frontend invariant eksik: '+$x)}}
foreach($x in @('VERIFY_R27_3_4_MESH_VERIFY_FIX.ps1','VERIFY_R27_3_5_TYPESCRIPT_CUTOVER_FIX.ps1')){if(-not (Test-Path (Join-Path $root ('scripts\'+$x)))){throw ('Cumulative gate eksik: '+$x)}}
Write-Host 'PASS: R27.3.6 cumulative production build repair.'
