param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [ValidateRange(1,1440)][int]$ValidForMinutes = 15,
  [ValidateRange(1,5000)][int]$MaxUses = 1
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$headers = @{ 'Authorization' = "Bearer $AccessToken" }
$body = @{ validForMinutes = $ValidForMinutes; maxUses = $MaxUses } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/enrollment-tokens" -Headers $headers -ContentType 'application/json' -Body $body
