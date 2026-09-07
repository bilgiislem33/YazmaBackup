param([switch]$PurgeIdentity)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'UNINSTALL_AGENT.ps1 yönetici olarak çalıştırılmalıdır.'
}
$serviceName = 'YazmaBackupAgent'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    & sc.exe delete $serviceName | Out-Null
}
$programRoot = Join-Path $env:ProgramFiles 'YazmaBackup'
if (Test-Path -LiteralPath $programRoot) { Remove-Item -LiteralPath $programRoot -Recurse -Force }
if ($PurgeIdentity) {
    $dataRoot = Join-Path $env:ProgramData 'YazmaBackup'
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force }
}
Write-Host 'YazmaBackup Agent kaldırıldı.'
