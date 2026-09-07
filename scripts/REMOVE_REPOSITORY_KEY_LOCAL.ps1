param(
  [Parameter(Mandatory=$true)][string]$AgentExe,
  [Parameter(Mandatory=$true)][string]$RepositoryRoot,
  [Parameter(Mandatory=$true)][string]$RepositoryId,
  [Parameter(Mandatory=$true)][string]$KeyId
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $AgentExe -PathType Leaf)) { throw "Agent bulunamadı: $AgentExe" }
& $AgentExe --remove-repository-key --repository-root $RepositoryRoot --repository-id $RepositoryId --key-id $KeyId
if ($LASTEXITCODE -ne 0) { throw "Repository key removal başarısız. ExitCode=$LASTEXITCODE" }
