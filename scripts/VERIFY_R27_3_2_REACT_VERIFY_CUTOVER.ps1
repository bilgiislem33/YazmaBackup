$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$verify=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\VERIFY.ps1')
if($verify.Contains('Get-Content .\src\YazmaBackup.ControlPlane\wwwroot\')){throw 'Eski wwwroot Get-Content gate ana VERIFY içinde kalmış.'}
if($verify.Contains("Join-Path $root 'src\YazmaBackup.ControlPlane\wwwroot\app.js'")){throw 'Eski app.js gate ana VERIFY içinde kalmış.'}
foreach($x in @('React Web UI / CSP / güvenli frontend','Legacy UI retirement / React single-source','LifecyclePage','frontendConsole')){
 if(-not $verify.Contains($x)){throw ('R27.3.2 React VERIFY invariant eksik: '+$x)}
}
Write-Host 'PASS: R27.3.2 authoritative VERIFY React single-source cutover.'
