param(
  [Parameter(Mandatory=$true)][string]$NodeId,
  [Parameter(Mandatory=$true)][string]$SharedSecurityStateDir,
  [Parameter(Mandatory=$true)][string]$DataProtectionCertificateThumbprint,
  [string]$ClusterId='production'
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($env:YAZMABACKUP_POSTGRES_CONNECTION)){
  throw 'YAZMABACKUP_POSTGRES_CONNECTION bu PowerShell oturumunda önceden güvenli şekilde tanımlanmalıdır. Connection stringi script parametresine veya komut geçmişine yazmayın.'
}
$state=[IO.Path]::GetFullPath($SharedSecurityStateDir)
if(-not(Test-Path -LiteralPath $state -PathType Container)){New-Item -ItemType Directory -Path $state -Force|Out-Null}
$thumb=($DataProtectionCertificateThumbprint -replace '\s','').ToUpperInvariant()
$cert=Get-Item -LiteralPath ('Cert:\LocalMachine\My\'+$thumb) -ErrorAction Stop
if(-not $cert.HasPrivateKey){throw 'Data Protection sertifikası private key içermelidir.'}
$env:YAZMABACKUP_HA_ROLE='active-active'
$env:YAZMABACKUP_STATE_ENGINE='postgresql'
$env:YAZMABACKUP_CLUSTER_ID=$ClusterId
$env:YAZMABACKUP_NODE_ID=$NodeId
$env:YAZMABACKUP_STATE_DIR=$state
$env:YAZMABACKUP_DP_CERT_THUMBPRINT=$thumb
Write-Host ('PASS: PostgreSQL transactional active-active environment hazır · Node='+$NodeId+' · Cluster='+$ClusterId)
Write-Host 'Connection string ekrana yazdırılmadı.'
Write-Host 'İlk başlangıçta PostgreSQL state row yoksa mevcut control-plane-state.json transaction içine bootstrap edilir.'
