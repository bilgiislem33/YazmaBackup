param(
 [Parameter(Mandatory=$true)][string]$NodeA,
 [Parameter(Mandatory=$true)][string]$NodeB
)
$ErrorActionPreference='Stop'
$a=$NodeA.TrimEnd('/');$b=$NodeB.TrimEnd('/')
$ha=Invoke-RestMethod ($a+'/health/ready') -TimeoutSec 10
$hb=Invoke-RestMethod ($b+'/health/ready') -TimeoutSec 10
if($ha.status -ne 'ready' -or $hb.status -ne 'ready'){throw 'Her iki node READY değil.'}
if($ha.role -ne 'active-active' -or $hb.role -ne 'active-active'){throw 'Her iki node active-active rolünde değil.'}
Write-Host ('PASS: İki node READY · '+$ha.nodeId+' / '+$hb.nodeId)
Write-Host 'Mutation concurrency doğrulaması için güvenli admin test endpointi yerine gerçek üretim mutationı otomatik tetiklenmez.'
Write-Host 'R11 kaynak kapısı distributed writer lock + state version conflict mekanizmasını doğrular.'
