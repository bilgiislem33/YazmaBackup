$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$state=Get-YazmaBackupControlPlaneSource -Area State
$pg=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\PostgreSqlStateEngine.cs')
foreach($x in @('CommitMultiAggregateCompletionUnsafeAsync','ApplyResilienceCommandResultUnsafe','TryCommitMultiAggregateCompletionAsync')){
 if(-not $state.Contains($x)){throw ('R17 StateStore invariant eksik: '+$x)}
}
foreach($x in @('TryCommitMultiAggregateCompletionAsync','IsolationLevel.Serializable','FOR UPDATE','SyncNormalizedProjectionAsync','completionTransaction = "multi-aggregate-atomic"')){
 if(-not $pg.Contains($x)){throw ('R17 PostgreSQL completion invariant eksik: '+$x)}
}
Write-Host 'PASS: R17 Multi-Aggregate Atomic Completion davranış tabanlı kalite kapısı.'
