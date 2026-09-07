param()
$ErrorActionPreference='Stop'
foreach($name in @('PGHOST','PGDATABASE','PGUSER')){
  if([string]::IsNullOrWhiteSpace((Get-Item -Path ('Env:\'+$name) -ErrorAction SilentlyContinue).Value)){throw ($name+' environment variable gereklidir.')}
}
$psql=Get-Command psql -ErrorAction SilentlyContinue
if(-not $psql){throw 'psql PATH içinde bulunamadı. PostgreSQL client tools gereklidir.'}

$required=@('yb_agents','yb_commands','yb_backup_policies','yb_audit_events','yb_alarms','yb_schema_migrations')
foreach($table in $required){
  $exists=& $psql.Source -X -q -t -A -c ("SELECT to_regclass('public."+$table+"') IS NOT NULL;")
  if($LASTEXITCODE -ne 0 -or ($exists.Trim() -ne 't')){throw ('Normalized PostgreSQL tablo eksik: '+$table)}
  Write-Host ('PASS: '+$table)
}
$version=& $psql.Source -X -q -t -A -c "SELECT version FROM yb_schema_migrations WHERE version='13.0';"
if($LASTEXITCODE -ne 0 -or ($version.Trim() -ne '13.0')){throw 'R13.0 schema migration kaydı eksik.'}
Write-Host 'PASS: Schema migration 13.0'

$indexes=& $psql.Source -X -q -t -A -c "SELECT count(*) FROM pg_indexes WHERE schemaname='public' AND indexname LIKE 'ix_yb_%';"
if($LASTEXITCODE -ne 0 -or [int]$indexes -lt 10){throw ('Normalized index sayısı beklenenden düşük: '+$indexes)}
Write-Host ('PASS: normalized index count='+$indexes)

$rows=& $psql.Source -X -q -t -A -c "SELECT (SELECT count(*) FROM yb_agents)||'|'||(SELECT count(*) FROM yb_commands)||'|'||(SELECT count(*) FROM yb_backup_policies)||'|'||(SELECT count(*) FROM yb_audit_events)||'|'||(SELECT count(*) FROM yb_alarms);"
if($LASTEXITCODE -ne 0){throw 'Normalized projection count sorgusu başarısız.'}
Write-Host ('PASS: projection counts Agents|Commands|Policies|Audit|Alarms = '+$rows.Trim())
Write-Host 'Not: PostgreSQL parolası komut satırına yazdırılmadı; PGPASSFILE veya kurumsal secret injection kullanın.'
