param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][string]$Path,
  [Parameter(Mandatory=$true)][string]$RepositoryRoot,
  [Parameter(Mandatory=$true)][string]$RepositoryId,
  [bool]$RequireSnapshot = $true,
  [long]$ActiveBytesPerSecond = 2097152,
  [long]$IdleBytesPerSecond = 0,
  [int]$UserIdleThresholdSeconds = 300,
  [int]$KeepLast = 30,
  [int]$KeepDaily = 14,
  [int]$KeepWeekly = 8,
  [int]$KeepMonthly = 12,
  [int]$ImmutabilityHours = 24,
  [bool]$ProtectionEnabled = $true,
  [int]$ProtectionMinimumChangedFiles = 100,
  [double]$ProtectionChangedFileRatio = 0.35,
  [int]$ProtectionMinimumExtensionChanges = 25,
  [double]$ProtectionExtensionChangeRatio = 0.20,
  [int]$ProtectionMinimumEntropySamples = 12,
  [double]$ProtectionHighEntropyRatio = 0.35,
  [double]$ProtectionEntropyThreshold = 7.45,
  [bool]$ProtectionAutoLock = $true,
  [string]$IdempotencyKey = ([Guid]::NewGuid().ToString('N')),
  [int]$PollSeconds = 3,
  [int]$MaxPolls = 1200
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$headers = @{ 'Authorization' = "Bearer $AccessToken"; 'X-YazmaBackup-Idempotency-Key' = $IdempotencyKey }
$retention = @{ keepLast = $KeepLast; keepDaily = $KeepDaily; keepWeekly = $KeepWeekly; keepMonthly = $KeepMonthly; immutabilityHours = $ImmutabilityHours }
$protection = @{
  enabled = $ProtectionEnabled
  minimumChangedFiles = $ProtectionMinimumChangedFiles
  changedFileRatioThreshold = $ProtectionChangedFileRatio
  minimumExtensionChanges = $ProtectionMinimumExtensionChanges
  extensionChangeRatioThreshold = $ProtectionExtensionChangeRatio
  minimumEntropySamples = $ProtectionMinimumEntropySamples
  highEntropyRatioThreshold = $ProtectionHighEntropyRatio
  entropyThresholdBitsPerByte = $ProtectionEntropyThreshold
  autoLockOnDetection = $ProtectionAutoLock
}
$body = @{
  path = $Path
  repositoryRoot = $RepositoryRoot
  repositoryId = $RepositoryId
  requireSnapshot = $RequireSnapshot
  activeBytesPerSecond = $ActiveBytesPerSecond
  idleBytesPerSecond = $IdleBytesPerSecond
  userIdleThresholdSeconds = $UserIdleThresholdSeconds
  retention = $retention
  protection = $protection
} | ConvertTo-Json -Depth 4
$queued = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/agents/$AgentId/backup" -Headers $headers -ContentType 'application/json' -Body $body
Write-Host "Yedek komutu kuyruğa alındı: $($queued.commandId)"
for ($i = 0; $i -lt $MaxPolls; $i++) {
    $result = Invoke-RestMethod -Method Get -Uri "$base/api/v1/admin/commands/$($queued.commandId)" -Headers $headers
    if ($result.completed) {
        if (-not $result.succeeded) { throw "Yedek başarısız: $($result.error)" }
        return ($result.resultJson | ConvertFrom-Json)
    }
    Start-Sleep -Seconds $PollSeconds
}
Write-Warning "Komut halen bekliyor. CommandId=$($queued.commandId)"
