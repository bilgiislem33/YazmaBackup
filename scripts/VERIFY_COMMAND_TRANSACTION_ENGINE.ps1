$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$state=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\StateStore.cs')
$pg=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\PostgreSqlStateEngine.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('ClaimNextCommandAsync','RenewCommandLeaseAsync','FOR UPDATE SKIP LOCKED','FOR UPDATE','lease_id uuid','lease_expires_at_utc','last_lease_renewal_utc','ix_yb_commands_claimable','ix_yb_commands_lease_expiry','Command exhausted after','commandLeaseEngine = "postgresql-skip-locked"')){
  if(-not $pg.Contains($x)){throw ('R16 PostgreSQL command transaction invariant eksik: '+$x)}
}
foreach($x in @('_directSqlMutations','ClaimNextCommandAsync','RenewCommandLeaseAsync','AcceptDatabaseSnapshotUnsafe')){
  if(-not $state.Contains($x)){throw ('R16 StateStore command transaction invariant eksik: '+$x)}
}
foreach($x in @('Lease Engine','commandLeaseEngine','R16 production active-active')){
  if(-not $ui.Contains($x)){throw ('R16 UI invariant eksik: '+$x)}
}
Write-Host 'PASS: R16 PostgreSQL Command Transaction Engine statik kalite kapısı.'
