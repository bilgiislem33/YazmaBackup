param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][string]$Version,
  [Parameter(Mandatory=$true)][string]$PackageUri,
  [Parameter(Mandatory=$true)][string]$Sha256,
  [Parameter(Mandatory=$true)][string]$SignatureBase64,
  [string]$IdempotencyKey = ([Guid]::NewGuid().ToString('N'))
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$headers = @{ 'Authorization' = "Bearer $AccessToken"; 'X-YazmaBackup-Idempotency-Key' = $IdempotencyKey }
$body = @{ version = $Version; packageUri = $PackageUri; sha256 = $Sha256; signatureBase64 = $SignatureBase64 } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/agents/$AgentId/stage-update" -Headers $headers -ContentType 'application/json' -Body $body
