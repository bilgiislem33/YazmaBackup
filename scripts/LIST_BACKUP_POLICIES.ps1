param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken
)
$ErrorActionPreference = 'Stop'
$headers = @{ 'Authorization' = "Bearer $AccessToken" }
Invoke-RestMethod -Method Get -Uri "$($Server.TrimEnd('/'))/api/v1/admin/policies" -Headers $headers
