param([Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[switch]$IncludeResolved)
$ErrorActionPreference='Stop'
$headers=@{Authorization="Bearer $AccessToken"}
$include=if($IncludeResolved){'true'}else{'false'}
Invoke-RestMethod -Method Get -Uri "$($Server.TrimEnd('/'))/api/v1/admin/alarms?limit=500&includeResolved=$include" -Headers $headers
