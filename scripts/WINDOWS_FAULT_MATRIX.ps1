param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][string]$SourcePath,
  [Parameter(Mandatory=$true)][string]$RepositoryRoot,
  [Parameter(Mandatory=$true)][string]$RepositoryId,
  [string]$EvidenceDirectory = '.\\validation-evidence'
)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
Write-Host '[1/3] Derin Windows/NAS doğrulaması'
$result=& (Join-Path $PSScriptRoot 'DEEP_VALIDATION.ps1') -Server $Server -AccessToken $AccessToken -AgentId $AgentId -SourcePath $SourcePath -RepositoryRoot $RepositoryRoot -RepositoryId $RepositoryId
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory "deep-validation-$stamp.json") -Encoding UTF8
Write-Host '[2/3] Self-healing teşhisi'
$diagnosis=& (Join-Path $PSScriptRoot 'SELF_HEAL.ps1') -Server $Server -AccessToken $AccessToken -AgentId $AgentId -SourcePath $SourcePath -RepositoryRoot $RepositoryRoot -RepositoryId $RepositoryId -Mode Diagnose
$diagnosis | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory "self-healing-$stamp.json") -Encoding UTF8
Write-Host '[3/3] Kanıt özeti'
$summary=[pscustomobject]@{generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');agentId=$AgentId;sourcePath=$SourcePath;repositoryRoot=$RepositoryRoot;repositoryId=$RepositoryId;deepValidationScore=$result.score;deepValidationGrade=$result.grade;selfHealingStatus=$diagnosis.overallStatus}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory "summary-$stamp.json") -Encoding UTF8
$summary
