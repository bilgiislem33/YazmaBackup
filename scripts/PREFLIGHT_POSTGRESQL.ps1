param(
  [Parameter(Mandatory=$true)][string]$Server,
  [Parameter(Mandatory=$true)][string]$Database,
  [Parameter(Mandatory=$true)][string]$AdminUser,
  [int]$Port = 5432
)
$ErrorActionPreference = 'Stop'
$psql = Get-Command psql -ErrorAction Stop
$version = & $psql.Source -X -q -t -A -v ON_ERROR_STOP=1 -h $Server -p $Port -U $AdminUser -d $Database -c "SHOW server_version;"
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL bağlantısı başarısız.' }
$version = ([string]($version | Select-Object -First 1)).Trim()
$checks = @(
  "SELECT CASE WHEN current_setting('server_encoding')='UTF8' THEN 'PASS' ELSE 'FAIL' END;",
  "SELECT CASE WHEN to_regtype('jsonb') IS NOT NULL THEN 'PASS' ELSE 'FAIL' END;",
  "SELECT CASE WHEN to_regtype('uuid') IS NOT NULL THEN 'PASS' ELSE 'FAIL' END;"
)
foreach ($sql in $checks) {
  $result = & $psql.Source -X -q -t -A -v ON_ERROR_STOP=1 -h $Server -p $Port -U $AdminUser -d $Database -c $sql
  if ($LASTEXITCODE -ne 0 -or ([string]($result | Select-Object -First 1)).Trim() -ne 'PASS') { throw "PostgreSQL preflight başarısız: $sql" }
}
Write-Host "PASS: PostgreSQL preflight. ServerVersion=$version, UTF8/jsonb/uuid hazır." -ForegroundColor Green
Write-Host 'Not: v1.2.0 paketinde PostgreSQL normalized runtime adapter henüz etkin değildir; bu preflight migration/cutover hazırlığını doğrular.'
