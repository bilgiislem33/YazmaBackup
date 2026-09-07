param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$Username,
  [string]$Name = 'PowerShell CLI',
  [ValidateSet('viewer','operator','backup-admin','security-admin','administrator')][string[]]$Roles = @('administrator'),
  [ValidateRange(1,2160)][int]$ValidForHours = 24
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$secure = Read-Host 'Yönetici parolası' -AsSecureString
$bstr = [IntPtr]::Zero
$password = $null
try {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $loginBody = @{ username = $Username; password = $password } | ConvertTo-Json
    $login = Invoke-RestMethod -Method Post -Uri "$base/api/v1/session/login" -WebSession $session -ContentType 'application/json' -Body $loginBody
    if (-not $login.csrfToken) { throw 'CSRF token alınamadı.' }
    if (-not ($login.user.roles -contains 'administrator')) { throw 'Management API token üretimi interaktif Administrator oturumu gerektirir.' }
    $headers = @{ 'X-YazmaBackup-CSRF' = [string]$login.csrfToken }
    $body = @{ name = $Name; roles = $Roles; validForHours = $ValidForHours } | ConvertTo-Json
    $issued = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/api-tokens" -WebSession $session -Headers $headers -ContentType 'application/json' -Body $body
    Write-Host "Management API Token üretildi. TokenId=$($issued.tokenId), Expires=$($issued.expiresAtUtc)"
    Write-Host 'Token yalnız bu yanıtta gösterilir. Güvenli secret store içine alın:'
    Write-Host "`$env:YAZMABACKUP_MANAGEMENT_TOKEN = '$($issued.token)'"
}
finally {
    $password = $null
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
