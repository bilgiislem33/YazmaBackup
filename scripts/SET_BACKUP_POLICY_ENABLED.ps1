param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][Guid]$PolicyId,
  [Parameter(Mandatory=$true)][bool]$Enabled
)
$ErrorActionPreference = 'Stop'
$headers = @{ 'Authorization' = "Bearer $AccessToken" }
$body = @{ enabled = $Enabled } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/admin/policies/$PolicyId/enabled" -Headers $headers -ContentType 'application/json' -Body $body
