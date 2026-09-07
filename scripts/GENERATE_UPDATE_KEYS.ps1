param(
  [Parameter(Mandatory=$true)][string]$PrivateKeyPath,
  [Parameter(Mandatory=$true)][string]$PublicKeyPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet run --project "$root\src\YazmaBackup.SigningTool\YazmaBackup.SigningTool.csproj" -c Release -- generate --private $PrivateKeyPath --public $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw 'Update signing key üretimi başarısız.' }
