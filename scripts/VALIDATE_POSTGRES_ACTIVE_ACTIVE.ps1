param(
 [Parameter(Mandatory=$true)][string]$NodeA,
 [Parameter(Mandatory=$true)][string]$NodeB,
 [Parameter(Mandatory=$true)][string]$ApiToken
)
$ErrorActionPreference='Stop'
$headers=@{Authorization=('Bearer '+$ApiToken)}
$nodes=@($NodeA.TrimEnd('/'),$NodeB.TrimEnd('/'))
$versions=@()
foreach($node in $nodes){
 $ready=Invoke-RestMethod ($node+'/health/ready') -TimeoutSec 10
 if($ready.status -ne 'ready' -or $ready.role -ne 'active-active'){throw ($node+' active-active READY değil.')}
 $status=Invoke-RestMethod ($node+'/api/v1/admin/state-engine/status') -Headers $headers -TimeoutSec 10
 if($status.engine -ne 'postgresql' -or -not $status.transactional){throw ($node+' PostgreSQL transactional engine kullanmıyor.')}
 $versions += [long]$status.database.version
 Write-Host ('PASS: '+$node+' · PostgreSQL · version='+$status.database.version)
}
if($versions[0] -ne $versions[1]){Write-Warning 'Node status okumaları arasında state version değişmiş olabilir; üretim mutation trafiğinde bu normaldir.'}
Write-Host 'PASS: İki Control Plane node PostgreSQL transactional active-active readiness kapısını geçti.'
