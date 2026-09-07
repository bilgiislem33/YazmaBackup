param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$AccessToken,
  [Parameter(Mandatory=$true)][Guid]$AgentId,
  [Parameter(Mandatory=$true)][string]$RepositoryId,
  [Parameter(Mandatory=$true)][string]$KeyId,
  [Parameter(Mandatory=$true)][string]$KeyFile,
  [bool]$MakeActive = $true,
  [string]$IdempotencyKey = ([Guid]::NewGuid().ToString('N')),
  [int]$PollSeconds = 3,
  [int]$MaxPolls = 120
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$headers = @{ 'Authorization' = "Bearer $AccessToken"; 'X-YazmaBackup-Idempotency-Key' = $IdempotencyKey }
$keyBytes = $null
$protected = $null
$entropy = $null
$keyBase64 = $null
$body = $null
try {
    $document = Get-Content -LiteralPath $KeyFile -Raw | ConvertFrom-Json
    if ($document.schemaVersion -ne '1' -or $document.protection -ne 'DPAPI-CurrentUser') { throw 'Desteklenmeyen repository key dosyası.' }
    if ($document.repositoryId -ne $RepositoryId -or $document.keyId -ne $KeyId) { throw 'Key dosyası RepositoryId/KeyId ile eşleşmiyor.' }
    $protected = [Convert]::FromBase64String([string]$document.protectedKeyBase64)
    $entropy = [Text.Encoding]::UTF8.GetBytes("YazmaBackup.RepositoryKeyFile.v1|$RepositoryId|$KeyId")
    $keyBytes = [Security.Cryptography.ProtectedData]::Unprotect($protected, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    if ($keyBytes.Length -ne 32) { throw 'Repository key 32 byte değil.' }
    $keyBase64 = [Convert]::ToBase64String($keyBytes)
    $body = @{ repositoryId = $RepositoryId; keyId = $KeyId; keyBase64 = $keyBase64; makeActive = $MakeActive } | ConvertTo-Json
    $queued = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/agents/$AgentId/repository-key" -Headers $headers -ContentType 'application/json' -Body $body
    Write-Host "Repository key provision komutu RSA-wrapped olarak kuyruğa alındı: $($queued.commandId)"
    for ($i = 0; $i -lt $MaxPolls; $i++) {
        $result = Invoke-RestMethod -Method Get -Uri "$base/api/v1/admin/commands/$($queued.commandId)" -Headers $headers
        if ($result.completed) {
            if (-not $result.succeeded) { throw "Repository key provision başarısız: $($result.error)" }
            return ($result.resultJson | ConvertFrom-Json)
        }
        Start-Sleep -Seconds $PollSeconds
    }
    Write-Warning "Komut halen bekliyor. CommandId=$($queued.commandId)"
}
finally {
    if ($keyBytes) { [Array]::Clear($keyBytes, 0, $keyBytes.Length) }
    if ($protected) { [Array]::Clear($protected, 0, $protected.Length) }
    if ($entropy) { [Array]::Clear($entropy, 0, $entropy.Length) }
    $keyBase64 = $null
    $body = $null
}
