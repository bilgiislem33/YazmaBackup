param(
  [Parameter(Mandatory=$true)][string]$NodeId,
  [Parameter(Mandatory=$true)][string]$SharedStateDir,
  [Parameter(Mandatory=$true)][string]$DataProtectionCertificateThumbprint
)
$ErrorActionPreference='Stop'
$state=[IO.Path]::GetFullPath($SharedStateDir)
if(-not (Test-Path -LiteralPath $state -PathType Container)){New-Item -ItemType Directory -Path $state -Force | Out-Null}
$thumb=($DataProtectionCertificateThumbprint -replace '\s','').ToUpperInvariant()
$cert=Get-Item -LiteralPath ('Cert:\LocalMachine\My\'+$thumb) -ErrorAction Stop
if(-not $cert.HasPrivateKey){throw 'Data Protection sertifikası private key içermelidir.'}
$probe=Join-Path $state ('.yb-r11-probe-'+[guid]::NewGuid().ToString('N'))
try{'r11'|Set-Content -LiteralPath $probe -Encoding ASCII;Remove-Item -LiteralPath $probe -Force}catch{throw ('Shared state root yazılamıyor: '+$_.Exception.Message)}
$env:YAZMABACKUP_HA_ROLE='active-active'
$env:YAZMABACKUP_NODE_ID=$NodeId
$env:YAZMABACKUP_STATE_DIR=$state
$env:YAZMABACKUP_DP_CERT_THUMBPRINT=$thumb
Write-Host ('PASS: R11 active-active node environment hazır · '+$NodeId)
Write-Host 'Not: Bu sürüm distributed single-writer commit lease + optimistic version conflict detection kullanır.'
