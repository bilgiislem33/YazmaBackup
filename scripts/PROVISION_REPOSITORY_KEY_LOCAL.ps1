param(
  [Parameter(Mandatory=$true)][string]$AgentExe,
  [Parameter(Mandatory=$true)][string]$RepositoryId,
  [Parameter(Mandatory=$true)][string]$KeyId,
  [Parameter(Mandatory=$true)][string]$KeyFile,
  [switch]$MakeActive
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $AgentExe -PathType Leaf)) { throw "Agent bulunamadı: $AgentExe" }
$keyBytes = $null
$protected = $null
$entropy = $null
try {
    $document = Get-Content -LiteralPath $KeyFile -Raw | ConvertFrom-Json
    if ($document.schemaVersion -ne '1' -or $document.protection -ne 'DPAPI-CurrentUser') { throw 'Desteklenmeyen repository key dosyası.' }
    if ($document.repositoryId -ne $RepositoryId -or $document.keyId -ne $KeyId) { throw 'Key dosyası RepositoryId/KeyId ile eşleşmiyor.' }
    $protected = [Convert]::FromBase64String([string]$document.protectedKeyBase64)
    $entropy = [Text.Encoding]::UTF8.GetBytes("YazmaBackup.RepositoryKeyFile.v1|$RepositoryId|$KeyId")
    $keyBytes = [Security.Cryptography.ProtectedData]::Unprotect($protected, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    if ($keyBytes.Length -ne 32) { throw 'Repository key 32 byte değil.' }
    $env:YAZMABACKUP_REPOSITORY_KEY_B64 = [Convert]::ToBase64String($keyBytes)
    $arguments = @('--provision-repository-key','--repository-id',$RepositoryId,'--key-id',$KeyId)
    if ($MakeActive) { $arguments += '--make-active' }
    & $AgentExe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Repository key provision başarısız. ExitCode=$LASTEXITCODE" }
}
finally {
    Remove-Item Env:YAZMABACKUP_REPOSITORY_KEY_B64 -ErrorAction SilentlyContinue
    if ($keyBytes) { [Array]::Clear($keyBytes, 0, $keyBytes.Length) }
    if ($protected) { [Array]::Clear($protected, 0, $protected.Length) }
    if ($entropy) { [Array]::Clear($entropy, 0, $entropy.Length) }
}
