param([Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[ValidateRange(1,1000)][int]$Limit=100)
$ErrorActionPreference='Stop'
Invoke-RestMethod -Method Get -Uri "$($Server.TrimEnd('/'))/api/v1/admin/recovery-runs?limit=$Limit" -Headers @{Authorization="Bearer $AccessToken"}
