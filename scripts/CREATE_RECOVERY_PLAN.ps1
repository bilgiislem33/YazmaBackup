param(
 [Parameter(Mandatory=$true)][string]$Server,
 [Parameter(Mandatory=$true)][string]$AccessToken,
 [Parameter(Mandatory=$true)][string]$Name,
 [Parameter(Mandatory=$true)][Guid[]]$PolicyIds,
 [ValidateRange(1,50)][int]$MaxParallelAgents=3,
 [ValidateRange(1,10080)][int]$RtoTargetMinutes=60,
 [ValidateRange(1,365)][int]$IntervalDays=30
)
$ErrorActionPreference='Stop'
$headers=@{Authorization="Bearer $AccessToken";'X-YazmaBackup-Idempotency-Key'=[Guid]::NewGuid().ToString('N')}
$body=@{name=$Name;policyIds=@($PolicyIds|ForEach-Object{$_.ToString('D')});maxParallelAgents=$MaxParallelAgents;rtoTargetMinutes=$RtoTargetMinutes;intervalDays=$IntervalDays;enabled=$true}|ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/admin/recovery-plans" -Headers $headers -ContentType 'application/json' -Body $body
