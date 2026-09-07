param(
  [Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][string]$RepositoryRoot,[Parameter(Mandatory=$true)][string]$RepositoryId,[Parameter(Mandatory=$true)][string]$BackupId,
  [Parameter(Mandatory=$true)][string]$SandboxRoot,[int]$MaxFiles=5000,[long]$MaxBytes=21474836480
)
$ErrorActionPreference='Stop';$base=$Server.TrimEnd('/');$headers=@{Authorization="Bearer $AccessToken";'X-YazmaBackup-Idempotency-Key'=[guid]::NewGuid().ToString()}
$body=@{repositoryRoot=$RepositoryRoot;repositoryId=$RepositoryId;backupId=$BackupId;sandboxRoot=$SandboxRoot;maxFiles=$MaxFiles;maxBytes=$MaxBytes}|ConvertTo-Json
$q=Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/agents/$AgentId/restore-sandbox" -Headers $headers -ContentType 'application/json' -Body $body
Write-Host "Restore sandbox kuyruğa alındı: $($q.commandId)"
