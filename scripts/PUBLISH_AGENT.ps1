param(
  [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content "$root\VERSION.json" -Raw | ConvertFrom-Json).version
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist "publish-$Runtime"
$bundle = Join-Path $dist "YazmaBackupAgent_v${version}_$Runtime"
$zip = "$bundle.zip"
Remove-Item -Recurse -Force $publish,$bundle -ErrorAction SilentlyContinue
Remove-Item -Force $zip -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish,(Join-Path $bundle 'agent') | Out-Null

dotnet publish "$root\src\YazmaBackup.Agent\YazmaBackup.Agent.csproj" -c Release -r $Runtime --self-contained true -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Agent publish başarısız.' }
Copy-Item "$publish\*" (Join-Path $bundle 'agent') -Recurse -Force
Copy-Item "$root\scripts\INSTALL_AGENT.ps1" $bundle -Force
Copy-Item "$root\scripts\UNINSTALL_AGENT.ps1" $bundle -Force
Compress-Archive -Path "$bundle\*" -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
Write-Host "Paket: $zip"
Write-Host "SHA256: $hash"
