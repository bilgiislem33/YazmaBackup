param([Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[Parameter(Mandatory=$true)][Guid]$AlarmId)
$ErrorActionPreference='Stop'
$headers=@{Authorization="Bearer $AccessToken"}
Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/admin/alarms/$AlarmId/acknowledge" -Headers $headers -ContentType 'application/json' -Body '{"acknowledge":true}'
