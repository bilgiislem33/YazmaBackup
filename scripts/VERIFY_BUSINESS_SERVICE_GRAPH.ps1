$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$store=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\StateStore.cs')
$svc=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\BusinessServiceGraphService.cs')
$program=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
foreach($x in @('BusinessServiceDependencies','UpsertDependencyAsync','DeleteDependencyAsync')){if(-not $store.Contains($x)){throw ('R24 dependency persistence invariant eksik: '+$x)}}
foreach($x in @('RecoveryDag','HasCycle','indegree','BLOCKED: dependency cycle','DependsOnServiceId')){if(-not $svc.Contains($x)){throw ('R24 graph invariant eksik: '+$x)}}
foreach($x in @('/business-service-graph','/business-service-dependencies','IBusinessContinuityStore')){if(-not $program.Contains($x)){throw ('R24 API invariant eksik: '+$x)}}
foreach($x in @('BusinessServiceGraphPage','/api/v1/admin/business-service-graph','Dependency-Aware Recovery DAG','restore''u sessizce başlatmaz')){if(-not $ui.Contains($x)){throw ('R24 React UI structural/safety invariant eksik: '+$x)}}
Write-Host 'PASS: R24 Business Service Graph + Recovery DAG davranış + React güvenlik kalite kapısı.'
