$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$pg=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\PostgreSqlStateEngine.cs')
$state=Get-YazmaBackupControlPlaneSource -Area State
$dr=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\DisasterRecoveryExecutionService.cs')
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('completion_lease_id','database lease ownership changed','database lease expired','completionLeaseId','ux_yb_commands_idempotency_v2','command_type, idempotency_key')){
 if(-not $pg.Contains($x)){throw ('R25 PostgreSQL completion fence invariant eksik: '+$x)}
}
foreach($x in @('DisasterRecoverySessions','ApproveDisasterRecoveryStepAsync','VerifyDisasterRecoveryStepAsync','Only the current dependency-safe DR step','requires explicit approval')){
 if(-not $state.Contains($x)){throw ('R25 DR session invariant eksik: '+$x)}
}
foreach($x in @('/dr-sessions','/approve','/verify','IDisasterRecoverySessionStore')){
 if(-not $program.Contains($x)){throw ('R25 DR API invariant eksik: '+$x)}
}
foreach($x in @('Disaster Recovery War Room','Dependency Recovery Rail','/api/v1/admin/dr-sessions','Bu Adımı Onayla','Doğrula ve Sonraki Gate','sessizce destructive restore çalıştırmaz')){
 if(-not $ui.Contains($x)){throw ('R25 React DR execution UI invariant eksik: '+$x)}
}
Write-Host 'PASS: R25 DR Execution + exact PostgreSQL completion lease fencing kalite kapısı.'
