param(
  [Parameter(Mandatory=$true)][string]$RepositoryId,
  [string]$KeyId = ("k-{0}-{1}" -f (Get-Date).ToUniversalTime().ToString('yyyyMMdd'), [Guid]::NewGuid().ToString('N').Substring(0,8)),
  [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if ($RepositoryId -notmatch '^[A-Za-z0-9_.-]{1,128}$') { throw 'RepositoryId geçersiz.' }
if ($KeyId -notmatch '^[A-Za-z0-9_.-]{1,128}$') { throw 'KeyId geçersiz.' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $dir = Join-Path $env:LOCALAPPDATA 'YazmaBackupAdmin\repository-keys'
    $OutputPath = Join-Path $dir ("{0}-{1}.ybkey" -f $RepositoryId, $KeyId)
}
$fullPath = [IO.Path]::GetFullPath($OutputPath)
$parent = Split-Path -Parent $fullPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
if (Test-Path -LiteralPath $fullPath) { throw "Key dosyası zaten var: $fullPath" }

$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$key = New-Object byte[] 32
$entropy = [Text.Encoding]::UTF8.GetBytes("YazmaBackup.RepositoryKeyFile.v1|$RepositoryId|$KeyId")
$protected = $null
try {
    $rng.GetBytes($key)
    $protected = [Security.Cryptography.ProtectedData]::Protect($key, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $document = [ordered]@{
        schemaVersion = '1'
        repositoryId = $RepositoryId
        keyId = $KeyId
        protection = 'DPAPI-CurrentUser'
        protectedKeyBase64 = [Convert]::ToBase64String($protected)
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json
    [IO.File]::WriteAllText($fullPath, $document, (New-Object Text.UTF8Encoding($false)))

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetOwner($identity)
    $acl.SetAccessRuleProtection($true, $false)
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)
    [void]$acl.AddAccessRule($rule)
    [IO.File]::SetAccessControl($fullPath, $acl)

    [pscustomobject]@{ repositoryId = $RepositoryId; keyId = $KeyId; keyFile = $fullPath }
}
finally {
    [Array]::Clear($key, 0, $key.Length)
    if ($protected) { [Array]::Clear($protected, 0, $protected.Length) }
    [Array]::Clear($entropy, 0, $entropy.Length)
    $rng.Dispose()
}
