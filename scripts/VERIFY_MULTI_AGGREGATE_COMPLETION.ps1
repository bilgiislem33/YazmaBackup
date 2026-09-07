$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$state=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\StateStore.cs')
$pg=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\PostgreSqlStateEngine.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('CommitMultiAggregateCompletionUnsafeAsync','ApplyResilienceCommandResultUnsafe','TryCommitMultiAggregateCompletionAsync')){
 if(-not $state.Contains($x)){throw ('R17 StateStore invariant eksik: '+$x)}
}
foreach($x in @('TryCommitMultiAggregateCompletionAsync','IsolationLevel.Serializable','FOR UPDATE','ProjectNormalizedStateAsync','completionTransaction = "multi-aggregate-atomic"')){
 if(-not $pg.Contains($x)){throw ('R17 PostgreSQL completion invariant eksik: '+$x)}
}
foreach($x in @('Completion','completionTransaction','R17 ile komutun tamamlanması')){
 if(-not $ui.Contains($x)){throw ('R17 UI invariant eksik: '+$x)}
}
Write-Host 'PASS: R17 Multi-Aggregate Atomic Completion statik kalite kapısı.'
