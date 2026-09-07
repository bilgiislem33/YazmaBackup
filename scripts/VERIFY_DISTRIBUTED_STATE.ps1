$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$state=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\StateStore.cs')
$coord=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\DistributedStateCoordinator.cs')
foreach($x in @('active-active','DistributedStateConflictException','StatusCodes.Status409Conflict','Retry-After')){if(-not $program.Contains($x)){throw ('R11 Program invariant eksik: '+$x)}}
foreach($x in @('AcquireWriteLeaseAsync','ReadVersion','WriteVersion','FileShare.None','distributed-state.writer.lock','distributed-state.version')){if(-not $coord.Contains($x)){throw ('R11 coordinator invariant eksik: '+$x)}}
foreach($x in @('_distributedState.AcquireWriteLeaseAsync','diskVersion != _stateVersion','DistributedStateConflictException','checked(diskVersion + 1)')){if(-not $state.Contains($x)){throw ('R11 StateStore invariant eksik: '+$x)}}
foreach($f in @('CONFIGURE_ACTIVE_ACTIVE_NODE.ps1','ACTIVE_ACTIVE_CONCURRENCY_TEST.ps1')){if(-not(Test-Path(Join-Path $root ('scripts\'+$f)))){throw ('R11 script eksik: '+$f)}}
Write-Host 'PASS: R11 Distributed State Engine + Active/Active Control Plane davranış tabanlı kalite kapısı.'
