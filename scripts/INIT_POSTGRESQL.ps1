param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$Database,
  [Parameter(Mandatory=$true)][string]$AdminUser,
  [int]$Port = 5432
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$migrationRoot = Join-Path $root 'database\postgresql'
if (-not (Test-Path -LiteralPath $migrationRoot)) { throw "PostgreSQL migration klasörü bulunamadı: $migrationRoot" }
$migrations = @(Get-ChildItem -LiteralPath $migrationRoot -File -Filter '*.sql' | Sort-Object Name)
if ($migrations.Count -eq 0) { throw 'PostgreSQL migration dosyası bulunamadı.' }
$psql = Get-Command psql -ErrorAction Stop

function Invoke-PsqlScalar([string]$Sql) {
    $output = & $psql.Source -X -q -t -A -v ON_ERROR_STOP=1 -h $Server -p $Port -U $AdminUser -d $Database -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL sorgusu başarısız. ExitCode=$LASTEXITCODE" }
    return ([string]($output | Select-Object -First 1)).Trim()
}

foreach ($migration in $migrations) {
    if ($migration.BaseName -notmatch '^(\d+)_') { throw "Migration adı sayısal önek içermiyor: $($migration.Name)" }
    $version = [int64]$Matches[1]
    $checksum = (Get-FileHash -LiteralPath $migration.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $tableExists = Invoke-PsqlScalar "SELECT CASE WHEN to_regclass('public.schema_migrations') IS NULL THEN '0' ELSE '1' END;"
    if ($tableExists -eq '1') {
        $existing = Invoke-PsqlScalar "SELECT checksum_sha256 FROM schema_migrations WHERE version=$version;"
        if (-not [string]::IsNullOrWhiteSpace($existing)) {
            if ($existing -ne $checksum) { throw "MIGRATION DRIFT: version=$version dosya=$($migration.Name) beklenen=$existing mevcut=$checksum" }
            Write-Host "SKIP: $($migration.Name) daha önce aynı checksum ile uygulanmış."
            continue
        }
    }

    Write-Host "PostgreSQL migration uygulanıyor: $($migration.Name) SHA256=$checksum"
    & $psql.Source -X -v ON_ERROR_STOP=1 -h $Server -p $Port -U $AdminUser -d $Database -f $migration.FullName
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL migration başarısız: $($migration.Name). ExitCode=$LASTEXITCODE" }
    $tableExists = Invoke-PsqlScalar "SELECT CASE WHEN to_regclass('public.schema_migrations') IS NULL THEN '0' ELSE '1' END;"
    if ($tableExists -ne '1') { throw 'schema_migrations tablosu migration sonrasında bulunamadı.' }
    $escapedChecksum = $checksum.Replace("'", "''")
    [void](Invoke-PsqlScalar "INSERT INTO schema_migrations(version, checksum_sha256) VALUES ($version, '$escapedChecksum') ON CONFLICT (version) DO NOTHING RETURNING version;")
    $recorded = Invoke-PsqlScalar "SELECT checksum_sha256 FROM schema_migrations WHERE version=$version;"
    if ($recorded -ne $checksum) { throw "Migration checksum kaydı doğrulanamadı: version=$version" }
}
Write-Host "PASS: $($migrations.Count) PostgreSQL migration dosyası checksum-drift korumasıyla doğrulandı." -ForegroundColor Green
