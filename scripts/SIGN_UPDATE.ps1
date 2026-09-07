param(
  [Parameter(Mandatory=$true)][string]$PrivateKeyPath,
  [Parameter(Mandatory=$true)][string]$Version,
  [Parameter(Mandatory=$true)][string]$PackagePath,
  [string]$OutputPath = "$PackagePath.signature.json"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet run --project "$root\src\YazmaBackup.SigningTool\YazmaBackup.SigningTool.csproj" -c Release -- sign --private $PrivateKeyPath --version $Version --package $PackagePath --output $OutputPath
if ($LASTEXITCODE -ne 0) { throw 'Update imzalama başarısız.' }
