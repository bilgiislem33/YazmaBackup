param(
  [Parameter(Mandatory=$true)][string]$Destination,
  [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$front=Join-Path $root 'src\YazmaBackup.Frontend'
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'BUILD_FRONTEND.ps1')}
$out=Join-Path $front 'out'
if(-not (Test-Path (Join-Path $out 'index.html'))){throw 'Frontend export bulunamadı.'}
if(Test-Path $Destination){Remove-Item -LiteralPath $Destination -Recurse -Force}
New-Item -ItemType Directory -Path $Destination -Force|Out-Null
Copy-Item -Path (Join-Path $out '*') -Destination $Destination -Recurse -Force
$sourceHash=(Get-FileHash (Join-Path $out 'index.html') -Algorithm SHA256).Hash
$destHash=(Get-FileHash (Join-Path $Destination 'index.html') -Algorithm SHA256).Hash
if($sourceHash -ne $destHash){throw 'Frontend publish artifact fingerprint uyuşmuyor.'}
Set-Content -LiteralPath (Join-Path $Destination '.yazmabackup-react-production') -Encoding ASCII -NoNewline -Value $sourceHash
Write-Host ('PASS: React production artifact publish/wwwroot içine bağlandı · '+$sourceHash)
