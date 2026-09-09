$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
foreach($x in @('RecoveryEvidenceTimeline','Recovery Evidence Timeline','uiText.disasterRecovery.title','uiText.disasterRecovery.rail','uiText.disasterRecovery.start','Doğrula ve Sonraki Gate')){
 if(-not $ui.Contains($x)){throw ('R27.2 frontend invariant eksik: '+$x)}
}
foreach($x in @('MapGet("/recovery-plans"','MapGet("/recovery-runs"','MapGet("/dr-sessions"','MapPost("/dr-sessions"','/steps/{order:int}/approve','/steps/{order:int}/verify')){
 if(-not $program.Contains($x)){throw ('R27.2 gerçek API invariant eksik: '+$x)}
}
if(-not $ui.Contains('Restore point uydurulmaz')){throw 'R27.2 truthfulness invariant eksik.'}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\wwwroot')){throw 'source wwwroot geri gelmiş.'}
if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\legacy-ui')){throw 'legacy-ui geri gelmiş.'}
Write-Host 'PASS: R27.2 Recovery Experience + DR War Room statik kalite kapısı.'
