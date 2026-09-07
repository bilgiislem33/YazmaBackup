param([Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[ValidateRange(1,5000)][int]$Limit=500)
$ErrorActionPreference='Stop'
$headers=@{Authorization="Bearer $AccessToken"}
Invoke-RestMethod -Method Get -Uri "$($Server.TrimEnd('/'))/api/v1/admin/repository-health?limit=$Limit" -Headers $headers
