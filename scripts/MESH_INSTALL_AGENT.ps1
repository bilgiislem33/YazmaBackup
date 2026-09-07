param(
  [Parameter(Mandatory=$true)][string]$PackageUrl,
  [Parameter(Mandatory=$true)][string]$PackageSha256,
  [Parameter(Mandatory=$true)][string]$Server,
  [string]$EnrollmentToken = $env:YAZMABACKUP_ENROLLMENT_TOKEN,
  [switch]$AllowInsecureHttp,
  [switch]$AllowLiveReadFallback,
  [string]$ServiceAccount = ''
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'MeshCentral görevi yönetici/SYSTEM yetkisiyle çalışmalıdır.'
}
if ($PackageSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'PackageSha256 geçersiz.' }
$uri = [Uri]$PackageUrl
if ($uri.Scheme -notin @('https','file')) { throw 'Agent paketi yalnız HTTPS veya file URI üzerinden alınabilir.' }

$work = Join-Path $env:ProgramData ("YazmaBackupDeploy\" + [Guid]::NewGuid().ToString('N'))
$zip = Join-Path $work 'agent.zip'
$extract = Join-Path $work 'bundle'
New-Item -ItemType Directory -Force -Path $work,$extract | Out-Null
try {
    if ($uri.Scheme -eq 'file') {
        Copy-Item -LiteralPath $uri.LocalPath -Destination $zip -Force
    } else {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $PackageUrl -OutFile $zip
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
    if ($actual -ne $PackageSha256) { throw "Agent paketi SHA-256 doğrulaması başarısız. Beklenen=$PackageSha256 Gelen=$actual" }
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $installer = Join-Path $extract 'INSTALL_AGENT.ps1'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Paket içinde INSTALL_AGENT.ps1 bulunamadı.' }

    $old = $env:YAZMABACKUP_ENROLLMENT_TOKEN
    try {
        if (-not [string]::IsNullOrWhiteSpace($EnrollmentToken)) { $env:YAZMABACKUP_ENROLLMENT_TOKEN = $EnrollmentToken }
        else { Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue }
        $installArgs = @{
            Server = $Server
            ServiceAccount = $ServiceAccount
        }
        if ($AllowInsecureHttp) { $installArgs['AllowInsecureHttp'] = $true }
        if ($AllowLiveReadFallback) { $installArgs['AllowLiveReadFallback'] = $true }
        & $installer @installArgs
    }
    finally {
        if ($null -eq $old) { Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue }
        else { $env:YAZMABACKUP_ENROLLMENT_TOKEN = $old }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
