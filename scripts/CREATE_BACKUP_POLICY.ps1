param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][string]$Name,
  [Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][string]$SourcePath,
  [Parameter(Mandatory=$true)][string]$RepositoryRoot,
  [Parameter(Mandatory=$true)][string]$RepositoryId,
  [int]$IntervalMinutes = 60,
  [bool]$RequireSnapshot = $true,
  [long]$ActiveBytesPerSecond = 2097152,
  [long]$IdleBytesPerSecond = 0,
  [int]$UserIdleThresholdSeconds = 300,
  [int]$KeepLast = 30,
  [int]$KeepDaily = 14,
  [int]$KeepWeekly = 8,
  [int]$KeepMonthly = 12,
  [int]$ImmutabilityHours = 24,
  [int]$RestoreDrillIntervalDays = 7,
  [bool]$ProtectionEnabled = $true,
  [int]$ProtectionMinimumChangedFiles = 100,
  [double]$ProtectionChangedFileRatio = 0.35,
  [int]$ProtectionMinimumExtensionChanges = 25,
  [double]$ProtectionExtensionChangeRatio = 0.20,
  [int]$ProtectionMinimumEntropySamples = 12,
  [double]$ProtectionHighEntropyRatio = 0.35,
  [double]$ProtectionEntropyThreshold = 7.45,
  [bool]$ProtectionAutoLock = $true,
  [bool]$Enabled = $true
)
$ErrorActionPreference = 'Stop'
$headers = @{ 'Authorization' = "Bearer $AccessToken" }
$body = @{
  name = $Name
  agentId = $AgentId
  sourcePath = $SourcePath
  repositoryRoot = $RepositoryRoot
  repositoryId = $RepositoryId
  requireSnapshot = $RequireSnapshot
  intervalMinutes = $IntervalMinutes
  activeBytesPerSecond = $ActiveBytesPerSecond
  idleBytesPerSecond = $IdleBytesPerSecond
  userIdleThresholdSeconds = $UserIdleThresholdSeconds
  retention = @{ keepLast = $KeepLast; keepDaily = $KeepDaily; keepWeekly = $KeepWeekly; keepMonthly = $KeepMonthly; immutabilityHours = $ImmutabilityHours }
  restoreDrillIntervalDays = $RestoreDrillIntervalDays
  protection = @{
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
  enabled = $Enabled
} | ConvertTo-Json -Depth 6
Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/admin/policies" -Headers $headers -ContentType 'application/json' -Body $body
