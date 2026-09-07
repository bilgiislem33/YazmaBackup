$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$gate=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\VERIFY_HA_DR.ps1')
$ha=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\ControlPlaneHaRuntime.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')

if($gate.Contains('dpapi-local-machine')){throw 'Eski R10.5 VERIFY literal gate geri gelmiş.'}
foreach($x in @('ReadyForTraffic','AcceptsMutations','portable')){
  if(-not $ha.Contains($x)){throw ('R10.5 current HA runtime invariant eksik: '+$x)}
}
foreach($x in @('YAZMABACKUP_HA_ROLE','YAZMABACKUP_DP_CERT_THUMBPRINT','ProtectKeysWithCertificate','/health/ready','/ha/status','/ha/drain','/ha/undrain')){
  if(-not $program.Contains($x)){throw ('R10.5 current Program invariant eksik: '+$x)}
}
Write-Host 'PASS: R27.3.9 R10.5 current HA behavior gate.'
