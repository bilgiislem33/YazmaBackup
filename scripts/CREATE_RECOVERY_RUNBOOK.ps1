param(
 [Parameter(Mandatory=$true)][string]$Server,
 [Parameter(Mandatory=$true)][string]$AccessToken,
 [Parameter(Mandatory=$true)][string]$Name,
 [Parameter(Mandatory=$true)][Guid[]]$RecoveryPlanIds,
 [ValidateRange(1,10080)][int]$RtoBudgetMinutes=240,
 [ValidateRange(1,365)][int]$IntervalDays=30
)
$ErrorActionPreference='Stop'
$headers=@{Authorization="Bearer $AccessToken";'X-YazmaBackup-Idempotency-Key'=[Guid]::NewGuid().ToString('N')}
$body=@{name=$Name;recoveryPlanIds=@($RecoveryPlanIds|ForEach-Object{$_.ToString('D')});rtoBudgetMinutes=$RtoBudgetMinutes;intervalDays=$IntervalDays;enabled=$true}|ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/admin/recovery-runbooks" -Headers $headers -ContentType 'application/json' -Body $body
