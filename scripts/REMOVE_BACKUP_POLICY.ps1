param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][Guid]$PolicyId
)
$ErrorActionPreference = 'Stop'
$headers = @{ 'Authorization' = "Bearer $AccessToken" }
Invoke-RestMethod -Method Delete -Uri "$($Server.TrimEnd('/'))/api/v1/admin/policies/$PolicyId" -Headers $headers
