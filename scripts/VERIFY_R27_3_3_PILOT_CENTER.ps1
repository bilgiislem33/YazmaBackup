$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
$verify=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\VERIFY.ps1')
foreach($x in @('PilotCenterPage','Kurulum & Pilot Merkezi','/api/v1/admin/pilot/readiness','/api/v1/admin/policy-templates','/apply-bulk','pilot-readiness-probe')){
 if(-not $ui.Contains($x)){throw ('React Pilot Center invariant eksik: '+$x)}
}
if($verify.Contains('$html')){throw 'Eski HTML UI VERIFY referansı kalmış.'}
Write-Host 'PASS: R27.3.3 React Enterprise Pilot Center.'
