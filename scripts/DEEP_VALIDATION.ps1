param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][string]$SourcePath,
  [Parameter(Mandatory=$true)][string]$RepositoryRoot,
  [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$RepositoryId
)
$ErrorActionPreference='Stop'
$base=$Server.TrimEnd('/')
$headers=@{Authorization="Bearer $AccessToken";'X-YazmaBackup-Idempotency-Key'=[guid]::NewGuid().ToString()}
$body=@{sourcePath=$SourcePath;repositoryRoot=$RepositoryRoot;repositoryId=$RepositoryId;requireSnapshot=$true;minimumFreeBytes=21474836480}|ConvertTo-Json
$q=Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/agents/$AgentId/deep-validation" -Headers $headers -ContentType 'application/json' -Body $body
for($i=0;$i -lt 180;$i++){Start-Sleep 1;$s=Invoke-RestMethod -Method Get -Uri "$base/api/v1/admin/commands/$($q.commandId)" -Headers @{Authorization="Bearer $AccessToken"};if($s.completed){if(-not $s.succeeded){throw $s.error};return ($s.resultJson|ConvertFrom-Json)}}
throw 'Derin doğrulama zaman aşımına uğradı.'
