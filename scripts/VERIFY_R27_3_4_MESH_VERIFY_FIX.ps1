$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
$verify=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\VERIFY.ps1')
foreach($x in @('function MeshPage','action(kind:"test"|"synchronize")','"/api/v1/admin/meshcentral/"+kind','/api/v1/admin/meshcentral/deploy')){
 if(-not $ui.Contains($x)){throw ('Mesh React invariant eksik: '+$x)}
}
if($verify.Contains("'meshcentral/test','meshcentral/synchronize'")){throw 'Dinamik endpointi literal arayan eski Mesh VERIFY gate kalmış.'}
Write-Host 'PASS: R27.3.4 MeshCentral React VERIFY dynamic-route fix.'
