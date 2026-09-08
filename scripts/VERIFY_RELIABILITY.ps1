$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$root=Split-Path -Parent $PSScriptRoot
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
$api=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\lib\api.ts')
$console=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('X-YazmaBackup-Correlation-ID','Server-Timing','TraceIdentifier')){if(-not $program.Contains($x)){throw ('R10.2 correlation invariant eksik: '+$x)}}
foreach($x in @('correlationId','30000','AbortController')){if(-not $api.Contains($x)){throw ('R10.2 API reliability invariant eksik: '+$x)}}
foreach($x in @('ReliabilityPage','Reliability Score','Production Diagnostic Matrix','Correlation & Failure Trace','SLO Bütçesi','/api/v1/admin/cluster','/api/v1/admin/repository-health?limit=100')){if(-not $console.Contains($x)){throw ('R10.2 observability invariant eksik: '+$x)}}
if(-not (Test-Path (Join-Path $root 'scripts\PRODUCTION_SMOKE_TEST.ps1'))){throw 'R10.2 production smoke test script eksik.'}
Write-Host 'PASS: R10.2 Production Reliability & Observability statik kalite kapısı.'
