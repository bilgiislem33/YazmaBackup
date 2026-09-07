param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$Username,
  [ValidateRange(1,20)][int]$Count = 8,
  [ValidateRange(1,90)][int]$ValidForDays = 30
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$secure = Read-Host 'Administrator parolası' -AsSecureString
$bstr = [IntPtr]::Zero
$password = $null
try {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-RestMethod -Method Post -Uri "$base/api/v1/session/login" -WebSession $session -ContentType 'application/json' -Body (@{ username=$Username; password=$password } | ConvertTo-Json)
    if (-not ($login.user.roles -contains 'administrator')) { throw 'Break-glass kodları yalnız Administrator için üretilebilir.' }
    $headers = @{ 'X-YazmaBackup-CSRF' = [string]$login.csrfToken }
    $body = @{ count=$Count; validForDays=$ValidForDays } | ConvertTo-Json
    $result = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/users/$($login.user.userId)/break-glass-codes" -WebSession $session -Headers $headers -ContentType 'application/json' -Body $body
    Write-Host "Break-glass kodları üretildi. Son geçerlilik: $($result.expiresAtUtc)"
    Write-Host 'Her kod tek kullanımlıktır; çevrimdışı güvenli kasada saklayın:'
    $result.codes | ForEach-Object { Write-Host $_ }
}
finally {
    $password = $null
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
