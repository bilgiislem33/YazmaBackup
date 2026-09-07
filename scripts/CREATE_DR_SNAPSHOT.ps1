param(
  [string]$StateDir=$env:YAZMABACKUP_STATE_DIR,
  [Parameter(Mandatory=$true)][string]$EncryptionCertificateThumbprint,
  [string]$OutputDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($StateDir)){throw 'StateDir veya YAZMABACKUP_STATE_DIR gereklidir.'}
$state=[IO.Path]::GetFullPath($StateDir)
if(-not (Test-Path -LiteralPath (Join-Path $state 'control-plane-state.json') -PathType Leaf)){throw 'control-plane-state.json bulunamadı.'}
$thumb=($EncryptionCertificateThumbprint -replace '\s','').ToUpperInvariant()
$cert=Get-Item -LiteralPath ('Cert:\LocalMachine\My\'+$thumb) -ErrorAction Stop
if(-not $cert.HasPrivateKey){Write-Warning 'Bu node private key görmüyor; export için public certificate yeterlidir ancak restore node private keye sahip olmalıdır.'}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$temp=Join-Path ([IO.Path]::GetTempPath()) ('YazmaBackup-DR-'+[guid]::NewGuid().ToString('N'))
$payload=Join-Path $temp 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
try{
  $includeFiles=@(
    'control-plane-state.json',
    'control-plane-state.json.bak',
    'global-nas-profile.protected',
    'autonomous-orchestration.protected',
    'recovery-evidence-signing-key.protected'
  )
  foreach($name in $includeFiles){
    $src=Join-Path $state $name
    if(Test-Path -LiteralPath $src -PathType Leaf){Copy-Item -LiteralPath $src -Destination (Join-Path $payload $name) -Force}
  }
  foreach($dirName in @('dataprotection-keys','repository-key-vault')){
    $src=Join-Path $state $dirName
    if(Test-Path -LiteralPath $src -PathType Container){Copy-Item -LiteralPath $src -Destination $payload -Recurse -Force}
  }

  $manifestItems=@()
  Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
    $relative=$_.FullName.Substring($payload.Length).TrimStart('\') -replace '\\','/'
    $manifestItems += [pscustomobject]@{
      path=$relative
      sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
      length=$_.Length
    }
  }
  if(@($manifestItems).Count -eq 0){throw 'DR snapshot için dosya bulunamadı.'}

  $manifest=[ordered]@{
    schemaVersion='1'
    product='YazmaBackup'
    createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
    sourceMachine=$env:COMPUTERNAME
    certificateThumbprint=$thumb
    fileCount=@($manifestItems).Count
    files=$manifestItems
  }
  $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $payload 'DR_MANIFEST.json') -Encoding UTF8

  $zip=Join-Path $temp 'snapshot.zip'
  Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
  if((Get-Item -LiteralPath $zip).Length -gt 512MB){throw 'DR bundle 512 MB sınırını aştı.'}
  $b64=Join-Path $temp 'snapshot.b64'
  [Convert]::ToBase64String([IO.File]::ReadAllBytes($zip),[Base64FormattingOptions]::InsertLineBreaks) | Set-Content -LiteralPath $b64 -Encoding ASCII

  $stamp=(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
  $out=Join-Path $OutputDirectory ('YazmaBackup_DR_'+$stamp+'.ybdr.p7m')
  Protect-CmsMessage -Path $b64 -To $cert -OutFile $out
  $hash=(Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant()
  Write-Host ('PASS: Şifreli DR snapshot oluşturuldu: '+$out)
  Write-Host ('SHA-256: '+$hash)
  Write-Host 'Not: Restore node aynı CMS/Data Protection sertifikasının private keyine sahip olmalıdır.'
} finally {
  Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
