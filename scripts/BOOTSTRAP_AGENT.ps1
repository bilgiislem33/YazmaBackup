param(
  [Parameter(Mandatory=$true)][string]$AgentExe,
  [Parameter(Mandatory=$true)][string]$Server,
  [string]$EnrollmentToken = $env:YAZMABACKUP_ENROLLMENT_TOKEN,
  [switch]$AllowInsecureHttp,
  [switch]$AllowLiveReadFallback,
  [string]$UpdatePublicKey
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $AgentExe -PathType Leaf)) { throw "Agent bulunamadı: $AgentExe" }

$old = $env:YAZMABACKUP_ENROLLMENT_TOKEN
try {
    if (-not [string]::IsNullOrWhiteSpace($EnrollmentToken)) { $env:YAZMABACKUP_ENROLLMENT_TOKEN = $EnrollmentToken }
    else { Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue }
    $arguments = @('--bootstrap', '--server', $Server)
    if ($AllowInsecureHttp) { $arguments += '--allow-insecure-http' }
    if ($AllowLiveReadFallback) { $arguments += '--allow-live-read-fallback' }
    if (-not [string]::IsNullOrWhiteSpace($UpdatePublicKey)) { $arguments += @('--update-public-key', $UpdatePublicKey) }
    & $AgentExe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Agent bootstrap başarısız. ExitCode=$LASTEXITCODE" }
}
finally {
    if ($null -eq $old) { Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue }
    else { $env:YAZMABACKUP_ENROLLMENT_TOKEN = $old }
}
