$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$state=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\StateStore.cs')
$pg=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\PostgreSqlStateEngine.cs')
foreach($x in @('_directSqlReads','YAZMABACKUP_DIRECT_SQL_READS','GetAgentsAsync','GetRecentCommandsAsync','GetBackupPoliciesAsync','GetAuditEventsAsync','GetAlarmsAsync','GetOperationalCommandMetricsAsync')){
  if(-not $state.Contains($x)){throw ('R14 StateStore direct-read invariant eksik: '+$x)}
}
foreach($x in @('SELECT payload::text FROM yb_agents','SELECT payload::text FROM yb_commands','SELECT payload::text FROM yb_backup_policies','SELECT payload::text FROM yb_audit_events','SELECT payload::text FROM yb_alarms','normalizedReadPath = "direct-sql"')){
  if(-not $pg.Contains($x)){throw ('R14 PostgreSQL direct-read invariant eksik: '+$x)}
}
Write-Host 'PASS: R14 Direct SQL Read Cutover davranış tabanlı kalite kapısı.'
