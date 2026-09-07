param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][Guid]$AgentId,
  [string]$Path = '::drives',
  [int]$PollSeconds = 2,
  [int]$MaxPolls = 60
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$headers = @{ 'Authorization' = "Bearer $AccessToken"; 'X-YazmaBackup-Idempotency-Key' = [Guid]::NewGuid().ToString('N') }
$body = @{ path = $Path } | ConvertTo-Json
$queued = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/agents/$AgentId/browse" -Headers $headers -ContentType 'application/json' -Body $body
Write-Host "Browse komutu kuyruğa alındı: $($queued.commandId)"
for ($i = 0; $i -lt $MaxPolls; $i++) {
    $result = Invoke-RestMethod -Method Get -Uri "$base/api/v1/admin/commands/$($queued.commandId)" -Headers $headers
    if ($result.completed) {
        if (-not $result.succeeded) { throw "Browse başarısız: $($result.error)" }
        return ($result.resultJson | ConvertFrom-Json)
    }
    Start-Sleep -Seconds $PollSeconds
}
Write-Warning "Komut halen bekliyor. CommandId=$($queued.commandId)"
