param()
$ErrorActionPreference='Stop'
foreach($name in @('PGHOST','PGDATABASE','PGUSER')){
  $item=Get-Item -Path ('Env:\'+$name) -ErrorAction SilentlyContinue
  if($null -eq $item -or [string]::IsNullOrWhiteSpace($item.Value)){throw ($name+' environment variable gereklidir.')}
}
$psql=Get-Command psql -ErrorAction SilentlyContinue
if(-not $psql){throw 'psql PATH içinde bulunamadı.'}
$cols=& $psql.Source -X -q -t -A -c "SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='yb_commands' AND column_name IN ('lease_id','lease_expires_at_utc','last_lease_renewal_utc');"
if($LASTEXITCODE -ne 0 -or [int]$cols -ne 3){throw 'R16 command lease kolonları eksik.'}
Write-Host 'PASS: command lease columns'
foreach($idx in @('ix_yb_commands_claimable','ix_yb_commands_lease_expiry')){
  $exists=& $psql.Source -X -q -t -A -c ("SELECT to_regclass('public."+$idx+"') IS NOT NULL;")
  if($LASTEXITCODE -ne 0 -or $exists.Trim() -ne 't'){throw ('R16 index eksik: '+$idx)}
  Write-Host ('PASS: '+$idx)
}
$stats=& $psql.Source -X -q -t -A -c "SELECT count(*) FILTER (WHERE completed_at_utc IS NULL), count(*) FILTER (WHERE completed_at_utc IS NULL AND lease_id IS NOT NULL), count(*) FILTER (WHERE completed_at_utc IS NULL AND lease_expires_at_utc<=now()) FROM yb_commands;"
if($LASTEXITCODE -ne 0){throw 'Command lease diagnostic query başarısız.'}
Write-Host ('PASS: Pending|Leased|Expired = '+$stats.Trim())
Write-Host 'Parolayı komut satırına yazmayın; PGPASSFILE veya kurumsal secret injection kullanın.'
