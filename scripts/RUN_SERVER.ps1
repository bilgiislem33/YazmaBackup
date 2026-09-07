$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stateDir = if ([string]::IsNullOrWhiteSpace($env:YAZMABACKUP_STATE_DIR)) { Join-Path $root 'src\YazmaBackup.ControlPlane\.local' } else { $env:YAZMABACKUP_STATE_DIR }
$env:YAZMABACKUP_STATE_DIR = $stateDir
if ([string]::IsNullOrWhiteSpace($env:YAZMABACKUP_AGENT_PACKAGE_ZIP) -or -not (Test-Path -LiteralPath $env:YAZMABACKUP_AGENT_PACKAGE_ZIP -PathType Leaf)) {
    $agentZip = Get-ChildItem -LiteralPath (Join-Path $root 'dist') -File -Filter 'YazmaBackupAgent_*_win-x64.zip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -ne $agentZip) {
        $env:YAZMABACKUP_AGENT_PACKAGE_ZIP = $agentZip.FullName
        Write-Host "Agent deployment paketi otomatik bulundu: $($agentZip.FullName)"
    }
}
$statePath = Join-Path $stateDir 'control-plane-state.json'
$bootstrapRequired = -not (Test-Path -LiteralPath $statePath)
if (-not $bootstrapRequired) {
    try {
        $existingState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($null -eq $existingState.managementUsers -or @($existingState.managementUsers.PSObject.Properties).Count -eq 0) { $bootstrapRequired = $true }
    } catch { }
}
if ($bootstrapRequired -and [string]::IsNullOrWhiteSpace($env:YAZMABACKUP_BOOTSTRAP_ADMIN_PASSWORD)) {
    throw 'İlk kurulum için bootstrap parolası yüklü değil. Önce .\scripts\GENERATE_SECRETS.ps1 çalıştırın ve aynı PowerShell penceresinde RUN_SERVER.ps1 başlatın.'
}
if ([string]::IsNullOrWhiteSpace($env:ASPNETCORE_URLS)) { $env:ASPNETCORE_URLS = 'http://127.0.0.1:5088' }
if ($env:YAZMABACKUP_ENABLE_LEGACY_ADMIN_KEY -eq 'true' -and [string]::IsNullOrWhiteSpace($env:YAZMABACKUP_ADMIN_KEY)) {
    throw 'Legacy AdminKey açık fakat YAZMABACKUP_ADMIN_KEY tanımlı değil.'
}
Write-Host "YazmaBackup Control Plane v1.2.0: $env:ASPNETCORE_URLS"
Write-Host 'Yönetim API erişimi için süreli Management API Token kullanılır. Legacy AdminKey varsayılan olarak kapalıdır.'
dotnet run --project "$root\src\YazmaBackup.ControlPlane\YazmaBackup.ControlPlane.csproj" -c Release
