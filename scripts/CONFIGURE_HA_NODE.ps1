param(
  [Parameter(Mandatory=$true)][ValidateSet('active','standby')][string]$Role,
  [Parameter(Mandatory=$true)][string]$NodeId,
  [Parameter(Mandatory=$true)][string]$SharedStateDir,
  [Parameter(Mandatory=$true)][string]$DataProtectionCertificateThumbprint
)
$ErrorActionPreference='Stop'
$state=[IO.Path]::GetFullPath($SharedStateDir)
if(-not (Test-Path -LiteralPath $state -PathType Container)){New-Item -ItemType Directory -Path $state -Force | Out-Null}
$thumb=($DataProtectionCertificateThumbprint -replace '\s','').ToUpperInvariant()
$cert=Get-Item -LiteralPath ('Cert:\LocalMachine\My\'+$thumb) -ErrorAction Stop
if(-not $cert.HasPrivateKey){throw 'Data Protection sertifikasının private key içermesi gerekir.'}
$probe=Join-Path $state ('.yb-ha-write-probe-'+[guid]::NewGuid().ToString('N'))
try{
  'probe' | Set-Content -LiteralPath $probe -Encoding ASCII
  Remove-Item -LiteralPath $probe -Force
}catch{throw ('Shared state root yazılabilir değil: '+$_.Exception.Message)}
$env:YAZMABACKUP_HA_ROLE=$Role
$env:YAZMABACKUP_NODE_ID=$NodeId
$env:YAZMABACKUP_STATE_DIR=$state
$env:YAZMABACKUP_DP_CERT_THUMBPRINT=$thumb
Write-Host ('PASS: HA runtime değişkenleri bu PowerShell oturumu için ayarlandı. Role='+$Role+' Node='+$NodeId)
Write-Host ('Shared state: '+$state)
Write-Host 'Kalıcı Windows Service ortam değişkenleri mevcut servis yöneticiniz üzerinden ayrıca tanımlanmalıdır.'
