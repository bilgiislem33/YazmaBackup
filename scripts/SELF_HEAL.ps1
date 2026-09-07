param(
  [Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][string]$SourcePath,[Parameter(Mandatory=$true)][string]$RepositoryRoot,[Parameter(Mandatory=$true)][string]$RepositoryId,
  [ValidateSet('Diagnose','ResetCircuit','CleanupRestoreTemp')][string]$Mode='Diagnose'
)
$ErrorActionPreference='Stop';$base=$Server.TrimEnd('/');$headers=@{Authorization="Bearer $AccessToken";'X-YazmaBackup-Idempotency-Key'=[guid]::NewGuid().ToString()}
if($Mode -eq 'Diagnose'){$uri="$base/api/v1/admin/agents/$AgentId/self-healing/diagnose";$body=@{sourcePath=$SourcePath;repositoryRoot=$RepositoryRoot;repositoryId=$RepositoryId}|ConvertTo-Json}
elseif($Mode -eq 'ResetCircuit'){$uri="$base/api/v1/admin/agents/$AgentId/self-healing/apply";$body=@{repositoryId=$RepositoryId;actionId='reset-repository-circuit';workingRoot=$null}|ConvertTo-Json}
else{$uri="$base/api/v1/admin/agents/$AgentId/self-healing/apply";$body=@{repositoryId=$RepositoryId;actionId='cleanup-stale-restore-temp';workingRoot=$SourcePath}|ConvertTo-Json}
$q=Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType 'application/json' -Body $body
for($i=0;$i -lt 120;$i++){Start-Sleep 1;$s=Invoke-RestMethod -Method Get -Uri "$base/api/v1/admin/commands/$($q.commandId)" -Headers @{Authorization="Bearer $AccessToken"};if($s.completed){if(-not $s.succeeded){throw $s.error};return ($s.resultJson|ConvertFrom-Json)}}
throw 'Self-healing işlemi zaman aşımına uğradı.'
