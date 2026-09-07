param(
  [Parameter(Mandatory=$true)][string]$BundlePath,
  [Parameter(Mandatory=$true)][string]$TargetStateDir,
  [switch]$Apply,
  [switch]$IConfirmControlPlaneIsStopped
)
$ErrorActionPreference='Stop'
if(-not (Test-Path -LiteralPath $BundlePath -PathType Leaf)){throw 'DR bundle bulunamadı.'}
$target=[IO.Path]::GetFullPath($TargetStateDir)
$temp=Join-Path ([IO.Path]::GetTempPath()) ('YazmaBackup-DR-Restore-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try{
  $b64=Join-Path $temp 'snapshot.b64'
  Unprotect-CmsMessage -Path $BundlePath | Set-Content -LiteralPath $b64 -Encoding ASCII
  $raw=((Get-Content -LiteralPath $b64 -Raw) -replace '\s','')
  $zip=Join-Path $temp 'snapshot.zip'
  [IO.File]::WriteAllBytes($zip,[Convert]::FromBase64String($raw))
  $extract=Join-Path $temp 'payload'
  Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force

  $manifestPath=Join-Path $extract 'DR_MANIFEST.json'
  if(-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)){throw 'DR_MANIFEST.json eksik.'}
  $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
  if($manifest.schemaVersion -ne '1' -or $manifest.product -ne 'YazmaBackup'){throw 'DR manifest schema/product geçersiz.'}

  foreach($item in @($manifest.files)){
    $path=Join-Path $extract (($item.path -replace '/','\'))
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw ('DR dosyası eksik: '+$item.path)}
    $actual=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual -ne $item.sha256){throw ('DR SHA-256 uyuşmazlığı: '+$item.path)}
  }
  Write-Host ('PASS: DR bundle integrity doğrulandı · '+$manifest.fileCount+' dosya · '+$manifest.createdAtUtc)

  if(-not $Apply){
    Write-Host 'VALIDATE-ONLY: Hedef state değiştirilmedi. Restore için -Apply -IConfirmControlPlaneIsStopped kullanın.'
    return
  }
  if(-not $IConfirmControlPlaneIsStopped){throw 'Offline restore için -IConfirmControlPlaneIsStopped zorunludur.'}

  New-Item -ItemType Directory -Path $target -Force | Out-Null
  $safety=Join-Path (Split-Path -Parent $target) ('YazmaBackup-state-before-restore-'+(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))
  if(Test-Path -LiteralPath $target){
    New-Item -ItemType Directory -Path $safety -Force | Out-Null
    Get-ChildItem -LiteralPath $target -Force -ErrorAction SilentlyContinue | Copy-Item -Destination $safety -Recurse -Force
  }
  foreach($name in @('control-plane-state.json','control-plane-state.json.bak','global-nas-profile.protected','autonomous-orchestration.protected','recovery-evidence-signing-key.protected')){
    $src=Join-Path $extract $name
    if(Test-Path -LiteralPath $src -PathType Leaf){Copy-Item -LiteralPath $src -Destination (Join-Path $target $name) -Force}
  }
  foreach($dirName in @('dataprotection-keys','repository-key-vault')){
    $src=Join-Path $extract $dirName
    if(Test-Path -LiteralPath $src -PathType Container){
      $dst=Join-Path $target $dirName
      if(Test-Path -LiteralPath $dst){Remove-Item -LiteralPath $dst -Recurse -Force}
      Copy-Item -LiteralPath $src -Destination $target -Recurse -Force
    }
  }
  Write-Host ('PASS: DR state offline restore tamamlandı. Safety copy: '+$safety)
  Write-Host 'Control Plane başlamadan önce aynı Data Protection/CMS sertifikasının private keyini LocalMachine\My store içinde doğrulayın.'
} finally {
  Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
