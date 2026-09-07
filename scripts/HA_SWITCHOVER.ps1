param(
  [Parameter(Mandatory=$true)][string]$CurrentActiveUri,
  [Parameter(Mandatory=$true)][string]$CandidateUri,
  [Parameter(Mandatory=$true)][string]$ApiToken,
  [switch]$DrainCurrentActive
)
$ErrorActionPreference='Stop'
$active=$CurrentActiveUri.TrimEnd('/')
$candidate=$CandidateUri.TrimEnd('/')
$headers=@{Authorization=('Bearer '+$ApiToken)}

$candidateLive=Invoke-RestMethod -Uri ($candidate+'/health/live') -Method Get -TimeoutSec 10
if($candidateLive.status -ne 'live'){throw 'Candidate node live health başarısız.'}
Write-Host ('PASS: Candidate live · '+$candidateLive.nodeId+' · role='+$candidateLive.role)

if($DrainCurrentActive){
  $null=Invoke-RestMethod -Uri ($active+'/api/v1/admin/ha/drain') -Method Post -Headers $headers -TimeoutSec 10
  Write-Host 'PASS: Current active drain edildi; load balancer readiness kontrolü bu nodeu havuzdan çıkarmalıdır.'
}

Write-Host ''
Write-Host 'PROMOTION GATE:'
Write-Host '1) Candidate node servis ortamında YAZMABACKUP_HA_ROLE=active ayarlayın.'
Write-Host '2) Candidate Control Plane servisini kontrollü yeniden başlatın.'
Write-Host '3) Ardından aşağıdaki health gate ile doğrulayın.'
Write-Host ('   Invoke-WebRequest '+$candidate+'/health/ready -UseBasicParsing')
Write-Host '4) health/ready HTTP 200 olmadan eski active nodeu kapatmayın.'
Write-Host ''
Write-Host 'Bu script iki nodeu aynı anda mutation-active yapmaz; shared JSON state için active/passive fail-closed model korunur.'
