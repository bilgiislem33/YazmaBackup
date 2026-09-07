param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][Guid]$IncidentId,
  [string]$IdempotencyKey = ([Guid]::NewGuid().ToString('N')),
  [int]$PollSeconds = 3,
  [int]$MaxPolls = 120
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$headers = @{ 'Authorization' = "Bearer $AccessToken"; 'X-YazmaBackup-Idempotency-Key' = $IdempotencyKey }
$body = @{ expectedIncidentId = $IncidentId } | ConvertTo-Json
$queued = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/agents/$AgentId/protection/clear" -Headers $headers -ContentType 'application/json' -Body $body
Write-Host "Koruma kilidi açma komutu kuyruğa alındı: $($queued.commandId)"
for ($i = 0; $i -lt $MaxPolls; $i++) {
    $result = Invoke-RestMethod -Method Get -Uri "$base/api/v1/admin/commands/$($queued.commandId)" -Headers $headers
    if ($result.completed) {
        if (-not $result.succeeded) { throw "Koruma kilidi açılamadı: $($result.error)" }
        return ($result.resultJson | ConvertFrom-Json)
    }
    Start-Sleep -Seconds $PollSeconds
}
Write-Warning "Komut halen bekliyor. CommandId=$($queued.commandId)"
