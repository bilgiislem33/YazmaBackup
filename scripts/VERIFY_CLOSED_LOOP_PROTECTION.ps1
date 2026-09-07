$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$svc=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\ClosedLoopProtectionService.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('ClosedLoopProtectionService','StartSafeDiagnosisAsync','diagnose-only','queued-diagnosis','awaiting-approval','verifying','verified')){
 if(-not $svc.Contains($x)){throw ('R21 closed-loop invariant eksik: '+$x)}
}
foreach($x in @('AddSingleton<ClosedLoopProtectionService>','/closed-loop-protection','/diagnose')){
 if(-not $program.Contains($x)){throw ('R21 API invariant eksik: '+$x)}
}
foreach($x in @('Closed-Loop Protection','Güvenli Teşhisi Başlat','ONAY BEKLİYOR','DOĞRULANIYOR','yönetici onayı')){
 if(-not $ui.Contains($x)){throw ('R21 UI invariant eksik: '+$x)}
}
Write-Host 'PASS: R21 Closed-Loop Protection Orchestrator statik kalite kapısı.'
