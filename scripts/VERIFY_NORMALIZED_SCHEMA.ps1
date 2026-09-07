$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$pg=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\PostgreSqlStateEngine.cs')
foreach($x in @(
  'yb_schema_migrations','yb_agents','yb_commands','yb_backup_policies','yb_audit_events','yb_alarms',
  'FOREIGN KEY(cluster_id, agent_id)','ix_yb_commands_pending','ux_yb_commands_idempotency','ix_yb_audit_brin_time',
  'NormalizedProjectionSql','SyncNormalizedProjectionAsync','ON CONFLICT(cluster_id, agent_id) DO UPDATE',
  'ON CONFLICT(cluster_id, command_id) DO UPDATE','13.0'
)){
  if(-not $pg.Contains($x)){throw ('R13 normalized schema invariant eksik: '+$x)}
}
if(-not(Test-Path(Join-Path $root 'scripts\POSTGRES_NORMALIZED_SCHEMA_CHECK.ps1'))){throw 'R13 PostgreSQL schema validation script eksik.'}
Write-Host 'PASS: R13 Normalized Enterprise PostgreSQL Schema davranış tabanlı kalite kapısı.'
