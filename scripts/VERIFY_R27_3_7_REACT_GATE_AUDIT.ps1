$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
$r103=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\VERIFY_AUTONOMOUS_RELIABILITY.ps1')
$r25=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\VERIFY_R25_DR_EXECUTION_CONSISTENCY.ps1')

if($r103.Contains('Canary / Rollout Gate') -or $r103.Contains('/self-healing/diagnose') -or $r103.Contains('Güvenli Circuit Reset')){
 throw 'R10.3 eski React metin gate kalıntısı bulundu.'
}
foreach($x in @('AutonomousReliabilityPage','Rollout Guard','Remediation State Machine','Preflight + Canary Başlat','Health Gate Sonrası Resume')){
 if(-not $ui.Contains($x)){throw ('R10.3 current React invariant eksik: '+$x)}
}
if($r25.Contains('Disaster Recovery Execution Controller') -or $r25.Contains('DR Execution Sessions')){
 throw 'R25 eski DR UI wording gate kalıntısı bulundu.'
}
foreach($x in @('Disaster Recovery War Room','Dependency Recovery Rail','Bu Adımı Onayla','Doğrula ve Sonraki Gate','sessizce destructive restore çalıştırmaz')){
 if(-not $ui.Contains($x)){throw ('R25 current React DR invariant eksik: '+$x)}
}
Write-Host 'PASS: R27.3.7 post-R26 React gate audit.'
