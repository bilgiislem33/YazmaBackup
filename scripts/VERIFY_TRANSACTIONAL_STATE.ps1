$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
$state=Get-YazmaBackupControlPlaneSource -Area State
$pg=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\PostgreSqlStateEngine.cs')
$proj=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\YazmaBackup.ControlPlane.csproj')
foreach($x in @('YAZMABACKUP_STATE_ENGINE','postgresql','active-active production mode requires','/state-engine/status')){if(-not $program.Contains($x)){throw ('R12 Program invariant eksik: '+$x)}}
foreach($x in @('PostgreSqlStateEngine','TryCommitAsync','_transactionalState','Transactional Control Plane state changed')){if(-not $state.Contains($x)){throw ('R12 StateStore invariant eksik: '+$x)}}
foreach($x in @('NpgsqlConnection','IsolationLevel.Serializable','UPDATE yazmabackup_control_plane_state','version = version + 1','WHERE cluster_id = @cluster AND version = @expected','ON CONFLICT (cluster_id) DO NOTHING','jsonb')){if(-not $pg.Contains($x)){throw ('R12 PostgreSQL invariant eksik: '+$x)}}
if(-not $proj.Contains('PackageReference Include="Npgsql"')){throw 'R12 Npgsql package reference eksik.'}
foreach($f in @('CONFIGURE_POSTGRES_ACTIVE_ACTIVE.ps1','VALIDATE_POSTGRES_ACTIVE_ACTIVE.ps1','CREATE_POSTGRES_DR_SNAPSHOT.ps1')){if(-not(Test-Path(Join-Path $root ('scripts\'+$f)))){throw ('R12 script eksik: '+$f)}}
$dr=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\CREATE_POSTGRES_DR_SNAPSHOT.ps1')
if($dr.Contains('--dbname=$env:YAZMABACKUP_POSTGRES_CONNECTION')){throw 'R12 DR script connection stringi process argumentına sızdırıyor.'}
if(-not $dr.Contains('Protect-CmsMessage')){throw 'R12 PostgreSQL DR encryption invariant eksik.'}
Write-Host 'PASS: R12 PostgreSQL Transactional State Engine + production Active/Active davranış tabanlı kalite kapısı.'
