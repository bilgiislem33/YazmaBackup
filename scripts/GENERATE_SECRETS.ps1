$ErrorActionPreference = 'Stop'
function New-Password([int]$Bytes = 24) {
    $buffer = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($buffer)
        return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+','A').Replace('/','b')
    }
    finally { $rng.Dispose(); [Array]::Clear($buffer, 0, $buffer.Length) }
}
$bootstrap = New-Password 24
$env:YAZMABACKUP_BOOTSTRAP_ADMIN_USERNAME = 'admin'
$env:YAZMABACKUP_BOOTSTRAP_ADMIN_PASSWORD = $bootstrap
Write-Host 'İlk kurulum için bootstrap yönetici bilgisi üretildi ve bu PowerShell sürecine yüklendi.'
Write-Host "Kullanıcı: $env:YAZMABACKUP_BOOTSTRAP_ADMIN_USERNAME"
Write-Host "Geçici parola: $bootstrap"
Write-Host 'Bu parola yalnız ilk giriş içindir; ilk girişte değiştirilmesi zorunludur.'
Write-Host 'Parolayı sohbet, ticket veya ekran görüntüsünde paylaşmayın. Sonrasında CREATE_MANAGEMENT_API_TOKEN.ps1 ile süreli API token üretin.'
Write-Host 'Legacy AdminKey yalnız geçiş/lab için YAZMABACKUP_ENABLE_LEGACY_ADMIN_KEY=true ile açıkça etkinleştirilebilir.'
$bootstrap = $null
