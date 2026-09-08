$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$state=Get-YazmaBackupControlPlaneSource -Area State
$pg=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\PostgreSqlStateEngine.cs')
foreach($x in @('_directSqlMutations','YAZMABACKUP_DIRECT_SQL_MUTATIONS','CommitCommandMutationUnsafeAsync','CommitPolicyMutationUnsafeAsync','CommitPolicyDeleteUnsafeAsync','AcceptFocusedCommitUnsafeAsync')){
 if(-not $state.Contains($x)){throw ('R15 StateStore mutation invariant eksik: '+$x)}
}
foreach($x in @('TryCommitCommandMutationAsync','TryCommitPolicyMutationAsync','TryCommitPolicyDeleteAsync','TryCommitFocusedMutationAsync','IsolationLevel.Serializable','version = version + 1','version = @expected')){
 if(-not $pg.Contains($x)){throw ('R15 PostgreSQL mutation invariant eksik: '+$x)}
}
Write-Host 'PASS: R15 Direct SQL Mutation Core Cutover davranis tabanli kalite kapisi.'
