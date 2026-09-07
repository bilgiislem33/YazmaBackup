param([Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[Parameter(Mandatory=$true)][Guid]$TokenId)
$ErrorActionPreference='Stop'
$headers=@{Authorization="Bearer $AccessToken"}
Invoke-RestMethod -Method Delete -Uri "$($Server.TrimEnd('/'))/api/v1/admin/api-tokens/$TokenId" -Headers $headers
