param(
  [string]$PublishedAgentPath = (Join-Path $PSScriptRoot 'agent'),
  [Parameter(Mandatory=$true)][string]$Server,
  [string]$EnrollmentToken = $env:YAZMABACKUP_ENROLLMENT_TOKEN,
  [switch]$AllowInsecureHttp,
  [switch]$AllowLiveReadFallback,
  [string]$UpdatePublicKey,
  [string]$ServiceAccount = '',
  [string]$ManagementAccessToken = '',
  [Guid]$ExpectedAgentId = [Guid]::Empty,
  [ValidateRange(30,300)][int]$PostUpdateHealthSeconds = 90
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'INSTALL_AGENT.ps1 yönetici olarak çalıştırılmalıdır.'
}
$agentSource = (Resolve-Path -LiteralPath $PublishedAgentPath).Path
$sourceExe = Join-Path $agentSource 'YazmaBackup.Agent.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Yayınlanmış Agent bulunamadı: $sourceExe" }

$serviceName = 'YazmaBackupAgent'
$version = '1.2.0'
$programRoot = Join-Path $env:ProgramFiles 'YazmaBackup'
$target = Join-Path $programRoot "Agent-$version"
$programDataRoot = Join-Path $env:ProgramData 'YazmaBackup'
$existingIdentity = Join-Path $programDataRoot 'agent.json'
$existingAccessToken = Join-Path $programDataRoot 'agent-access-token.dpapi'
$existingConfig = Join-Path $programDataRoot 'agent-config.json'
$existingRepositoryKeys = Join-Path $programDataRoot 'repository-keys'
$existingNasCredentials = Join-Path $programDataRoot 'nas-credentials'
$inPlaceUpgrade = (Test-Path -LiteralPath $existingIdentity -PathType Leaf) -and (Test-Path -LiteralPath $existingAccessToken -PathType Leaf)
if ($inPlaceUpgrade) {
    Write-Host 'INFO: Mevcut Agent kimliği bulundu. In-place upgrade modu: AgentId/token/config/repository keys/NAS credentials korunacak.'
}


function ConvertTo-NativeCommandLineArgument {
    param([AllowEmptyString()][string]$Value)
    if ($null -eq $Value -or $Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }

    # Windows CommandLineToArgvW-compatible quoting. This keeps paths/ACL expressions exact
    # without invoking cmd.exe or PowerShell's native stderr adapter.
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq '\') {
            $slashes++
            continue
        }
        if ($ch -eq '"') {
            [void]$builder.Append(('\' * (($slashes * 2) + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)); $slashes = 0 }
        [void]$builder.Append($ch)
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * ($slashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory=$true)][string]$Operation,
        [Parameter(Mandatory=$true)][string]$FilePath,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [switch]$AllowNonZero
    )

    # R5.6: NEVER invoke recovery utilities through `& ... 2>&1` while
    # $ErrorActionPreference='Stop'. Windows PowerShell can promote native stderr
    # (for example icacls /C 'Access is denied') to NativeCommandError before we can
    # inspect LASTEXITCODE. ProcessStartInfo captures stdout/stderr as plain text and
    # makes exit-code handling deterministic on Turkish/English Windows alike.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeCommandLineArgument ([string]$_) }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    try {
        if (-not $process.Start()) { throw "$Operation başlatılamadı: $FilePath" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally { $process.Dispose() }

    $parts = @()
    if (-not [string]::IsNullOrWhiteSpace($stdout)) { $parts += ($stdout.Trim() -replace '[\r\n]+',' | ') }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) { $parts += ('stderr=' + ($stderr.Trim() -replace '[\r\n]+',' | ')) }
    $detail = $parts -join ' | '

    if ($exitCode -ne 0 -and -not $AllowNonZero) {
        throw "$Operation başarısız. ExitCode=$exitCode; Output=$detail"
    }
    [pscustomobject]@{ ExitCode = $exitCode; Detail = $detail }
}

New-Item -ItemType Directory -Force -Path $programRoot,$target | Out-Null
if (-not (Test-Path -LiteralPath $programDataRoot -PathType Container -ErrorAction SilentlyContinue)) {
    try { New-Item -ItemType Directory -Force -Path $programDataRoot | Out-Null }
    catch {
        # Erişim sapması nedeniyle klasör mevcut olduğu halde New-Item göremiyorsa önce yalnız kökü kurtar.
        # Recursive işlem root ACL düzelmeden yapılmaz.
        $createError = $_
        $earlyTakeown = Invoke-NativeCapture -Operation 'ProgramData early root ownership recovery' -FilePath 'takeown.exe' -Arguments @('/F',$programDataRoot) -AllowNonZero
        if ($earlyTakeown.ExitCode -eq 0) {
            Invoke-NativeCapture -Operation 'ProgramData early root ACL reset' -FilePath 'icacls.exe' -Arguments @($programDataRoot,'/reset') -AllowNonZero | Out-Null
            Invoke-NativeCapture -Operation 'ProgramData early root private ACL grant' -FilePath 'icacls.exe' -Arguments @($programDataRoot,'/grant:r','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F') -AllowNonZero | Out-Null
        }
        if (-not (Test-Path -LiteralPath $programDataRoot -PathType Container -ErrorAction SilentlyContinue)) { throw $createError }
    }
}

# R5.6: ProgramData ACL recovery is root-first; native stderr is captured without PowerShell NativeCommandError promotion.
# A deny/owner drift on the root can make a recursive icacls call fail before it repairs anything.
# Therefore repair the root first, then recover descendants, and only then enforce the final private ACL.
function Set-PrivateAclOnRoot {
    param([Parameter(Mandatory=$true)][string]$Path)

    # Current deployment identity is normally SYSTEM. Taking ownership to the current identity
    # avoids depending on membership-token behaviour of BUILTIN\Administrators.
    Invoke-NativeCapture -Operation 'ProgramData root ownership recovery' -FilePath 'takeown.exe' -Arguments @('/F',$Path) | Out-Null

    # Reset ONLY the root first. No wildcard and no recursion at this stage.
    Invoke-NativeCapture -Operation 'ProgramData root ACL reset' -FilePath 'icacls.exe' -Arguments @($Path,'/reset') | Out-Null
    Invoke-NativeCapture -Operation 'ProgramData root inheritance removal' -FilePath 'icacls.exe' -Arguments @($Path,'/inheritance:r') | Out-Null
    Invoke-NativeCapture -Operation 'ProgramData root private ACL grant' -FilePath 'icacls.exe' -Arguments @($Path,'/grant:r','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F') | Out-Null
    Invoke-NativeCapture -Operation 'ProgramData root SYSTEM owner set' -FilePath 'icacls.exe' -Arguments @($Path,'/setowner','*S-1-5-18') | Out-Null
}

function Repair-YazmaBackupProgramDataAcl {
    param([Parameter(Mandatory=$true)][string]$Path)

    Set-PrivateAclOnRoot -Path $Path

    # Root is now traversable. Recover descendants without aborting on a single stale/locked item;
    # a second strict validation below decides whether the endpoint is safe to continue.
    $takeownTree = Invoke-NativeCapture -Operation 'ProgramData descendant ownership recovery' -FilePath 'takeown.exe' -Arguments @('/F',$Path,'/R','/D','Y') -AllowNonZero
    $grantTree = Invoke-NativeCapture -Operation 'ProgramData descendant emergency grant' -FilePath 'icacls.exe' -Arguments @($Path,'/grant:r','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F','/T','/C') -AllowNonZero
    $resetTree = Invoke-NativeCapture -Operation 'ProgramData descendant ACL reset' -FilePath 'icacls.exe' -Arguments @($Path,'/reset','/T','/C') -AllowNonZero
    $inheritTree = Invoke-NativeCapture -Operation 'ProgramData descendant inheritance removal' -FilePath 'icacls.exe' -Arguments @($Path,'/inheritance:r','/T','/C') -AllowNonZero
    $finalTree = Invoke-NativeCapture -Operation 'ProgramData descendant private ACL grant' -FilePath 'icacls.exe' -Arguments @($Path,'/grant:r','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F','/T','/C') -AllowNonZero
    $ownerTree = Invoke-NativeCapture -Operation 'ProgramData descendant SYSTEM owner set' -FilePath 'icacls.exe' -Arguments @($Path,'/setowner','*S-1-5-18','/T','/C') -AllowNonZero

    # Reassert the root after recursive /reset because /reset can restore parent inheritance on the root.
    Set-PrivateAclOnRoot -Path $Path

    # Identity files are security-critical and must be individually writable/deletable if present.
    foreach ($critical in @('agent.json','agent-access-token.dpapi','update-public-key.pem')) {
        $criticalPath = Join-Path $Path $critical
        if (Test-Path -LiteralPath $criticalPath -ErrorAction SilentlyContinue) {
            Invoke-NativeCapture -Operation "Critical identity ownership recovery: $critical" -FilePath 'takeown.exe' -Arguments @('/F',$criticalPath) | Out-Null
            Invoke-NativeCapture -Operation "Critical identity ACL reset: $critical" -FilePath 'icacls.exe' -Arguments @($criticalPath,'/reset') | Out-Null
            Invoke-NativeCapture -Operation "Critical identity inheritance removal: $critical" -FilePath 'icacls.exe' -Arguments @($criticalPath,'/inheritance:r') | Out-Null
            Invoke-NativeCapture -Operation "Critical identity private ACL: $critical" -FilePath 'icacls.exe' -Arguments @($criticalPath,'/grant:r','*S-1-5-18:F','*S-1-5-32-544:F') | Out-Null
            Invoke-NativeCapture -Operation "Critical identity SYSTEM owner: $critical" -FilePath 'icacls.exe' -Arguments @($criticalPath,'/setowner','*S-1-5-18') | Out-Null
        }
    }

    # Mandatory real create/write/read/delete proof before Agent bootstrap.
    $probe = Join-Path $Path ('.acl-write-probe-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $probeText = 'YazmaBackup ACL write probe ' + [DateTimeOffset]::UtcNow.ToString('O')
        [IO.File]::WriteAllText($probe, $probeText, (New-Object Text.UTF8Encoding($false)))
        $readBack = [IO.File]::ReadAllText($probe)
        if ($readBack -ne $probeText) { throw 'ACL write probe read-back doğrulaması başarısız.' }
        Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $probe -PathType Leaf) { throw 'ACL write probe silinemedi.' }
    }
    catch {
        $rootAcl = (Invoke-NativeCapture -Operation 'ProgramData ACL diagnostic' -FilePath 'icacls.exe' -Arguments @($Path) -AllowNonZero).Detail
        $diag = @(
            "takeownTree=$($takeownTree.ExitCode):$($takeownTree.Detail)",
            "grantTree=$($grantTree.ExitCode):$($grantTree.Detail)",
            "resetTree=$($resetTree.ExitCode):$($resetTree.Detail)",
            "inheritTree=$($inheritTree.ExitCode):$($inheritTree.Detail)",
            "finalTree=$($finalTree.ExitCode):$($finalTree.Detail)",
            "ownerTree=$($ownerTree.ExitCode):$($ownerTree.Detail)",
            "rootAcl=$rootAcl"
        ) -join ' || '
        throw "ProgramData ACL preflight başarısız: $($_.Exception.Message); $diag"
    }
    finally { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue }

    Write-Host 'PASS: R5.6 ProgramData ACL recovery + deterministic native exit-code handling + create/write/read/delete preflight başarılı.'
}

Repair-YazmaBackupProgramDataAcl -Path $programDataRoot

# ACL düzeltmesinden sonra enrollment dosyalarını değerlendir; böylece stale DENY nedeniyle
# installer daha self-heal aşamasına gelmeden Test-Path ile düşmez.
$identityExists = Test-Path -LiteralPath $existingIdentity -PathType Leaf
$tokenExists = Test-Path -LiteralPath $existingAccessToken -PathType Leaf
if ((-not $identityExists -or -not $tokenExists) -and [string]::IsNullOrWhiteSpace($EnrollmentToken)) {
    throw 'İlk kurulum için EnrollmentToken veya YAZMABACKUP_ENROLLMENT_TOKEN gerekli.'
}
foreach ($knownIdentityFile in @($existingIdentity,$existingAccessToken)) {
    if (Test-Path -LiteralPath $knownIdentityFile -PathType Leaf) {
        try { (Get-Item -LiteralPath $knownIdentityFile -Force).IsReadOnly = $false } catch { }
    }
}

# Enrollment kanıtı yarım kalmışsa iki dosyayı birlikte yenile. Tek taraflı stale identity/token
# bir sonraki bootstrap'ı bozmasın. Tam çift varsa upgrade/reinstall için korunur.
if (-not [string]::IsNullOrWhiteSpace($EnrollmentToken) -and ($identityExists -xor $tokenExists)) {
    Remove-Item -LiteralPath $existingIdentity -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $existingAccessToken -Force -ErrorAction SilentlyContinue
    Write-Host 'INFO: Yarım kalmış Agent identity/token çifti temizlendi; güvenli yeniden enrollment uygulanacak.'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$oldImagePath = $null
$createdService = $false
if ($null -ne $service) {
    $oldImagePath = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name ImagePath).ImagePath
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
}

Remove-Item -Recurse -Force "$target\*" -ErrorAction SilentlyContinue
Copy-Item "$agentSource\*" $target -Recurse -Force
$exe = Join-Path $target 'YazmaBackup.Agent.exe'

$persistentUpdateKey = Join-Path $programDataRoot 'update-public-key.pem'
if (-not [string]::IsNullOrWhiteSpace($UpdatePublicKey)) {
    if (-not (Test-Path -LiteralPath $UpdatePublicKey -PathType Leaf)) { throw "Update public key bulunamadı: $UpdatePublicKey" }
    Copy-Item -LiteralPath $UpdatePublicKey -Destination $persistentUpdateKey -Force
}

$oldEnrollment = $env:YAZMABACKUP_ENROLLMENT_TOKEN
try {
    if (-not [string]::IsNullOrWhiteSpace($EnrollmentToken)) { $env:YAZMABACKUP_ENROLLMENT_TOKEN = $EnrollmentToken }
    else { Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue }
    $bootstrap = @('--bootstrap','--server',$Server,'--update-public-key',$persistentUpdateKey)
    if ($AllowInsecureHttp) { $bootstrap += '--allow-insecure-http' }
    if ($AllowLiveReadFallback) { $bootstrap += '--allow-live-read-fallback' }
    & $exe @bootstrap
    if ($LASTEXITCODE -ne 0) { throw "Agent bootstrap başarısız. ExitCode=$LASTEXITCODE" }
}
finally {
    if ($null -eq $oldEnrollment) { Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue }
    else { $env:YAZMABACKUP_ENROLLMENT_TOKEN = $oldEnrollment }
}

$imagePath = "`"$exe`" --service"
$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"

function Get-YazmaBackupServiceWmiObject {
    param([Parameter(Mandatory=$true)][string]$Name)
    $safeName = $Name.Replace("'", "''")
    $searcher = New-Object System.Management.ManagementObjectSearcher("SELECT * FROM Win32_Service WHERE Name='$safeName'")
    try {
        $rows = @($searcher.Get())
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    }
    finally { $searcher.Dispose() }
}

function Invoke-ServiceWmiChange {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string]$PathName,
        [string]$StartName = ''
    )

    # R5.7: service ImagePath/account updates no longer use sc.exe command-line parsing.
    # Win32_Service.Change receives named typed parameters, so quoted Program Files paths,
    # LocalSystem and gMSA identities cannot be corrupted by PowerShell/sc.exe tokenization.
    $svc = Get-YazmaBackupServiceWmiObject -Name $Name
    if ($null -eq $svc) { throw "Windows servisi WMI üzerinden bulunamadı: $Name" }
    try {
        $inParams = $svc.GetMethodParameters('Change')
        $inParams['PathName'] = $PathName
        $inParams['StartMode'] = 'Automatic'
        if (-not [string]::IsNullOrWhiteSpace($StartName)) {
            $inParams['StartName'] = $StartName
            $inParams['StartPassword'] = $null
        }
        $result = $svc.InvokeMethod('Change', $inParams, $null)
        $returnValue = [int]$result.Properties['ReturnValue'].Value
        if ($returnValue -ne 0) {
            throw "Win32_Service.Change başarısız. ReturnValue=$returnValue; Service=$Name; PathName=$PathName; StartName=$StartName"
        }
    }
    finally { $svc.Dispose() }
}

function New-GmsaServiceWmi {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string]$PathName,
        [Parameter(Mandatory=$true)][string]$Account
    )
    $serviceClass = New-Object System.Management.ManagementClass('Win32_Service')
    try {
        $inParams = $serviceClass.GetMethodParameters('Create')
        $inParams['Name'] = $Name
        $inParams['DisplayName'] = 'YazmaBackup Agent'
        $inParams['PathName'] = $PathName
        $inParams['ServiceType'] = 16 # SERVICE_WIN32_OWN_PROCESS
        $inParams['ErrorControl'] = 1 # SERVICE_ERROR_NORMAL
        $inParams['StartMode'] = 'Automatic'
        $inParams['DesktopInteract'] = $false
        $inParams['StartName'] = $Account
        $inParams['StartPassword'] = $null
        $result = $serviceClass.InvokeMethod('Create', $inParams, $null)
        $returnValue = [int]$result.Properties['ReturnValue'].Value
        if ($returnValue -ne 0) {
            throw "Win32_Service.Create gMSA başarısız. ReturnValue=$returnValue; Service=$Name; Account=$Account"
        }
    }
    finally { $serviceClass.Dispose() }
}

function Remove-ServiceWmi {
    param([Parameter(Mandatory=$true)][string]$Name)
    $svc = Get-YazmaBackupServiceWmiObject -Name $Name
    if ($null -eq $svc) { return }
    try {
        $result = $svc.InvokeMethod('Delete', $null, $null)
        $returnValue = [int]$result.Properties['ReturnValue'].Value
        if ($returnValue -ne 0) { Write-Warning "Win32_Service.Delete ReturnValue=$returnValue; Service=$Name" }
    }
    finally { $svc.Dispose() }
}

if ($null -eq $service) {
    if ([string]::IsNullOrWhiteSpace($ServiceAccount) -or $ServiceAccount -eq 'LocalSystem') {
        # Default LocalSystem creation remains on New-Service; it takes BinaryPathName as a .NET string.
        $createError = $null
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            try {
                New-Service -Name $serviceName -BinaryPathName $imagePath -DisplayName 'YazmaBackup Agent' -StartupType Automatic -ErrorAction Stop | Out-Null
                $createError = $null
                break
            }
            catch {
                $createError = $_
                if ($attempt -lt 5) { Start-Sleep -Seconds 2 }
            }
        }
        if ($null -ne $createError) {
            throw "Windows servisi oluşturulamadı (New-Service, 5 deneme). $($createError.Exception.Message)"
        }
    } else {
        if (-not $ServiceAccount.EndsWith('$')) { throw 'Parolasız özel servis hesabı için gMSA hesabı ($ ile biten) kullanılmalıdır.' }
        New-GmsaServiceWmi -Name $serviceName -PathName $imagePath -Account $ServiceAccount
    }
    $createdService = $true
} else {
    if ([string]::IsNullOrWhiteSpace($ServiceAccount)) {
        # Existing account is intentionally preserved. Only executable path + Automatic start are changed.
        Invoke-ServiceWmiChange -Name $serviceName -PathName $imagePath
    } elseif ($ServiceAccount -eq 'LocalSystem') {
        Invoke-ServiceWmiChange -Name $serviceName -PathName $imagePath -StartName 'LocalSystem'
    } else {
        if (-not $ServiceAccount.EndsWith('$')) { throw 'Parolasız özel servis hesabı için gMSA hesabı ($ ile biten) kullanılmalıdır.' }
        Invoke-ServiceWmiChange -Name $serviceName -PathName $imagePath -StartName $ServiceAccount
    }
}

# Automatic (Delayed Start): Start=2 + DelayedAutoStart=1.
# Böylece create/config sırasında sc.exe delayed-auto yorumlama farkları devre dışı kalır.
Set-ItemProperty -LiteralPath $serviceRegistryPath -Name DelayedAutoStart -Type DWord -Value 1 -ErrorAction Stop
Set-ItemProperty -LiteralPath $serviceRegistryPath -Name Description -Type String -Value 'YazmaBackup kurumsal endpoint yedekleme ajanı' -ErrorAction Stop
$recoveryConfig = Invoke-NativeCapture -Operation 'Servis recovery policy ayarlama' -FilePath 'sc.exe' -Arguments @('failure',$serviceName,'reset=','86400','actions=','restart/5000/restart/15000/restart/60000') -AllowNonZero
if ($recoveryConfig.ExitCode -ne 0) { Write-Warning "Servis recovery policy uygulanamadı; deployment bloklanmadı. ExitCode=$($recoveryConfig.ExitCode); $($recoveryConfig.Detail)" }
$failureFlag = Invoke-NativeCapture -Operation 'Servis failure flag ayarlama' -FilePath 'sc.exe' -Arguments @('failureflag',$serviceName,'1') -AllowNonZero
if ($failureFlag.ExitCode -ne 0) { Write-Warning "Servis failure flag uygulanamadı; deployment bloklanmadı. ExitCode=$($failureFlag.ExitCode); $($failureFlag.Detail)" }

# SCM tarafında gerçekten oluştuğunu doğrula; sessiz create/config başarısını kabul etme.
$service = Get-Service -Name $serviceName -ErrorAction Stop
$serviceRegistry = Get-ItemProperty -LiteralPath $serviceRegistryPath -ErrorAction Stop
$imagePathStored = [string]$serviceRegistry.ImagePath
$startStored = [int]$serviceRegistry.Start
$delayedStored = [int]$serviceRegistry.DelayedAutoStart
$objectNameStored = [string]$serviceRegistry.ObjectName
if ([string]::IsNullOrWhiteSpace($imagePathStored) -or $imagePathStored -notmatch [Regex]::Escape('YazmaBackup.Agent.exe')) {
    throw "Windows servisi ImagePath doğrulamasını geçemedi. ImagePath=$imagePathStored"
}
if ($startStored -ne 2 -or $delayedStored -ne 1) {
    throw "Windows servisi startup doğrulamasını geçemedi. Start=$startStored; DelayedAutoStart=$delayedStored"
}
if ($ServiceAccount -eq 'LocalSystem' -and $objectNameStored -ne 'LocalSystem') {
    throw "Windows servisi hesap doğrulamasını geçemedi. Beklenen=LocalSystem; Gerçek=$objectNameStored"
}
if (-not [string]::IsNullOrWhiteSpace($ServiceAccount) -and $ServiceAccount -ne 'LocalSystem' -and $objectNameStored -ne $ServiceAccount) {
    throw "Windows servisi gMSA hesap doğrulamasını geçemedi. Beklenen=$ServiceAccount; Gerçek=$objectNameStored"
}
Write-Host "PASS: R5.7 Windows service configuration WMI/.NET path ile doğrulandı; ImagePath/Start/DelayedAutoStart/Account SCM registry kanıtı alındı. Account=$objectNameStored"

$healthStartedAtUtc = [DateTimeOffset]::UtcNow
$installLogRoot = Join-Path $programDataRoot 'Logs'
New-Item -ItemType Directory -Force -Path $installLogRoot | Out-Null
$serviceDiagnosticLog = Join-Path $installLogRoot ('service-health-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.log')
try {
    Start-Service -Name $serviceName
    $running = Get-Service -Name $serviceName
    $running.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    Start-Sleep -Seconds 5
    $running = Get-Service -Name $serviceName -ErrorAction Stop
    if ($running.Status -ne 'Running') { throw "YazmaBackupAgent servisi kararlılık penceresinde durdu. State=$($running.Status)" }

    if (-not [string]::IsNullOrWhiteSpace($ManagementAccessToken) -and $ExpectedAgentId -ne [Guid]::Empty) {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($PostUpdateHealthSeconds)
        $healthy = $false
        do {
            try {
                $agents = Invoke-RestMethod -Method Get -Uri ($Server.TrimEnd('/') + '/api/v1/admin/agents') -Headers @{ Authorization = "Bearer $ManagementAccessToken" }
                $row = @($agents) | Where-Object { [Guid]$_.agentId -eq $ExpectedAgentId } | Select-Object -First 1
                if ($null -ne $row -and [string]$row.agentVersion -eq $version -and [DateTimeOffset]$row.lastSeenUtc -ge $healthStartedAtUtc.AddSeconds(-5)) { $healthy = $true; break }
            } catch { }
            Start-Sleep -Seconds 3
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        if (-not $healthy) { throw "Post-update health check başarısız: Agent $ExpectedAgentId Control Plane'de $version heartbeat'i göstermedi." }
        Write-Host "PASS: Post-update heartbeat/version doğrulandı: Agent=$ExpectedAgentId Version=$version"
    }
}
catch {
    $updateError = $_
    $diagnostics = New-Object System.Collections.Generic.List[string]
    $diagnostics.Add('Error=' + $updateError.Exception.Message)
    try { $scQuery = Invoke-NativeCapture -Operation 'SC query diagnostic' -FilePath 'sc.exe' -Arguments @('queryex',$serviceName) -AllowNonZero; $diagnostics.Add('SC_QUERY=' + $scQuery.Detail) } catch { }
    try {
        $events = Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Service Control Manager'; StartTime=(Get-Date).AddMinutes(-10) } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'YazmaBackupAgent|YazmaBackup.Agent' } |
            Select-Object -First 5
        foreach ($event in $events) { $diagnostics.Add(('SCM_EVENT=' + ($event.Message -replace "[\r\n]+", ' '))) }
    } catch { }
    try {
        $agentLog = Get-ChildItem (Join-Path $programDataRoot 'logs') -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($null -ne $agentLog) { $diagnostics.Add('AGENT_LOG=' + $agentLog.FullName); $diagnostics.AddRange([string[]](Get-Content -LiteralPath $agentLog.FullName -Tail 20 -ErrorAction SilentlyContinue)) }
    } catch { }
    $diagnostics | Set-Content -LiteralPath $serviceDiagnosticLog -Encoding UTF8
    if (-not [string]::IsNullOrWhiteSpace($oldImagePath)) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        try { Invoke-ServiceWmiChange -Name $serviceName -PathName ([string]$oldImagePath) } catch { Write-Warning "Rollback WMI service change başarısız: $($_.Exception.Message)" }
        Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        Write-Warning 'Yeni sürüm sağlık kontrolünü geçemedi; servis eski ImagePath ile geri alındı.'
    } elseif ($createdService) {
        Remove-ServiceWmi -Name $serviceName
    }
    throw ("$($updateError.Exception.Message) Tanılama: $serviceDiagnosticLog")
}

# Yalnız servis + opsiyonel Control Plane health-check başarılı olduktan sonra eski sürüm dizinlerini kaldır.
Get-ChildItem -LiteralPath $programRoot -Directory -Filter 'Agent-*' | Where-Object { $_.FullName -ne $target } | Remove-Item -Recurse -Force
& cmdkey.exe /delete:YazmaBackup/AgentAccessToken 2>$null | Out-Null
Write-Host "PASS: YazmaBackup Agent $version servisi çalışıyor."
if ($inPlaceUpgrade) { Write-Host 'PASS: In-place upgrade tamamlandı; Control Plane yedek politikaları yeniden oluşturulmadı ve mevcut Agent kimliği korundu.' }
