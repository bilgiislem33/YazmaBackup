param(
  [Parameter(Mandatory=$true)][string]$StateFile,
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$Database,
  [Parameter(Mandatory=$true)][string]$AdminUser,
  [int]$Port = 5432
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $StateFile)) { throw "State dosyası bulunamadı: $StateFile" }
$psql = Get-Command psql -ErrorAction Stop
$raw = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $StateFile))
try {
    $jsonText = [Text.Encoding]::UTF8.GetString($raw)
    $document = $jsonText | ConvertFrom-Json
    if ([int]$document.schemaVersion -ne 7) { throw "Yalnız state schema 7 staging destekleniyor. Bulunan=$($document.schemaVersion)" }
    $checksum = (Get-FileHash -LiteralPath $StateFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $snapshotId = [Guid]::NewGuid().ToString()
    $base64 = [Convert]::ToBase64String($raw)
    $sql = @"
BEGIN;
INSERT INTO control_plane_state_snapshots(snapshot_id, source_schema_version, source_sha256, source_json, cutover_status)
VALUES ('$snapshotId'::uuid, 7, '$checksum', convert_from(decode('$base64','base64'),'UTF8')::jsonb, 'staged')
ON CONFLICT (source_sha256) DO NOTHING;
COMMIT;
SELECT snapshot_id::text || '|' || source_sha256 || '|' || cutover_status
FROM control_plane_state_snapshots WHERE source_sha256='$checksum';
"@
    $result = $sql | & $psql.Source -X -q -t -A -v ON_ERROR_STOP=1 -h $Server -p $Port -U $AdminUser -d $Database
    if ($LASTEXITCODE -ne 0) { throw "State staging başarısız. ExitCode=$LASTEXITCODE" }
    $line = ([string]($result | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1)).Trim()
    if ($line -notmatch [Regex]::Escape($checksum)) { throw 'PostgreSQL staging checksum doğrulaması başarısız.' }
    Write-Host "PASS: JSON state PostgreSQL staging alanına doğrulanmış snapshot olarak aktarıldı. $line" -ForegroundColor Green
    Write-Host 'Bu işlem runtime cutover YAPMAZ. v1.2.0 normalized PostgreSQL runtime adapter etkinleşmeden state dosyasını silmeyin.'
}
finally {
    if ($raw) { [Array]::Clear($raw, 0, $raw.Length) }
    $jsonText = $null
    $base64 = $null
}
