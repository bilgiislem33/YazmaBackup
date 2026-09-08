$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
$ha=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\ControlPlaneHaRuntime.cs')
$console=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')

foreach($token in @('YAZMABACKUP_HA_ROLE','YAZMABACKUP_DP_CERT_THUMBPRINT','ProtectKeysWithCertificate','/health/ready','/ha/status','/ha/drain','/ha/undrain')){
  if(-not $program.Contains($token)){throw ('R10.5 HA invariant eksik: '+$token)}
}
foreach($token in @('ReadyForTraffic','AcceptsMutations','portable')){
  if(-not $ha.Contains($token)){throw ('R10.5 HA runtime invariant eksik: '+$token)}
}
foreach($token in @('HA & Disaster Recovery','Data Protection','Repository Key Vault','Node''u Drain Et')){
  if(-not $console.Contains($token)){throw ('R10.5 HA/DR UI invariant eksik: '+$token)}
}
foreach($file in @('CONFIGURE_HA_NODE.ps1','CREATE_DR_SNAPSHOT.ps1','RESTORE_DR_SNAPSHOT.ps1','HA_SWITCHOVER.ps1')){
  if(-not (Test-Path (Join-Path $root ('scripts\'+$file)))){throw ('R10.5 script eksik: '+$file)}
}
$drExport=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\CREATE_DR_SNAPSHOT.ps1')
$drRestore=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\RESTORE_DR_SNAPSHOT.ps1')
foreach($token in @('Protect-CmsMessage','DR_MANIFEST.json','Get-FileHash','repository-key-vault','dataprotection-keys')){
  if(-not $drExport.Contains($token)){throw ('R10.5 DR export invariant eksik: '+$token)}
}
foreach($token in @('Unprotect-CmsMessage','DR_MANIFEST.json','Get-FileHash','IConfirmControlPlaneIsStopped')){
  if(-not $drRestore.Contains($token)){throw ('R10.5 DR restore invariant eksik: '+$token)}
}
if(-not $program.Contains('if (haRole != "standby") builder.Services.AddHostedService<PolicySchedulerService>();')){
  throw 'R10.5 standby background-mutator fail-closed invariant eksik.'
}
Write-Host 'PASS: R10.5 Enterprise HA + Zero-Downtime/Drain + DR statik kalite kapısı.'
