param(
  [Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][string]$RepositoryRoot,[Parameter(Mandatory=$true)][string]$RepositoryId,[Parameter(Mandatory=$true)][string]$BackupId,
  [Parameter(Mandatory=$true)][string]$DestinationRoot,[Parameter(Mandatory=$true)][string[]]$IncludePaths,[switch]$OverwriteExisting
)
$ErrorActionPreference='Stop';$base=$Server.TrimEnd('/');$headers=@{Authorization="Bearer $AccessToken";'X-YazmaBackup-Idempotency-Key'=[guid]::NewGuid().ToString()}
$body=@{repositoryRoot=$RepositoryRoot;repositoryId=$RepositoryId;backupId=$BackupId;destinationRoot=$DestinationRoot;includePaths=$IncludePaths;overwriteExisting=[bool]$OverwriteExisting}|ConvertTo-Json -Depth 4
$q=Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/agents/$AgentId/granular-restore" -Headers $headers -ContentType 'application/json' -Body $body
Write-Host "Granular restore kuyruğa alındı: $($q.commandId)"
