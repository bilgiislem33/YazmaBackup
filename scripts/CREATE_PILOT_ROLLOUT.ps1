param(
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$Server,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$AccessToken,
    [ValidateRange(1,1440)][int]$ValidForMinutes = 30,
    [ValidateRange(1,5000)][int]$MaxUses = 50
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$body = @{ validForMinutes=$ValidForMinutes; maxUses=$MaxUses } | ConvertTo-Json
$result = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/enrollment-tokens" -Headers @{ Authorization = "Bearer $AccessToken" } -ContentType 'application/json' -Body $body
if([string]::IsNullOrWhiteSpace([string]$result.enrollmentToken)) { throw 'Control Plane enrollment token döndürmedi.' }
$env:YAZMABACKUP_ENROLLMENT_TOKEN = [string]$result.enrollmentToken
Write-Host 'Pilot enrollment token mevcut PowerShell oturumuna güvenli biçimde yüklendi.' -ForegroundColor Green
Write-Host ("GrantId={0}  SonGeçerlilik={1}  MaxUses={2}" -f $result.grantId,$result.expiresAtUtc,$result.maxUses)
Write-Host 'Token değeri konsola yazdırılmadı. Bu PowerShell penceresini rollout bitene kadar açık tutun.'
Write-Host 'Rollout bittikten sonra: Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN'
