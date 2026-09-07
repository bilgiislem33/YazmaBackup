param(
 [Parameter(Mandatory=$true)][string]$Server,
 [Parameter(Mandatory=$true)][string]$AccessToken,
 [Parameter(Mandatory=$true)][Guid]$AgentId,
 [Parameter(Mandatory=$true)][Guid]$PolicyId,
 [Parameter(Mandatory=$true)][string]$RepositoryRoot,
 [Parameter(Mandatory=$true)][string]$RepositoryId,
 [switch]$Execute
)
$ErrorActionPreference='Stop'
if(-not $Execute){throw 'Bu lab fault-injection testi yalnız -Execute ile çalışır.'}
if(-not $RepositoryId.StartsWith('LAB-',[StringComparison]::Ordinal)){throw 'RepositoryId LAB- ile başlamalıdır.'}
if($RepositoryRoot -notmatch 'YazmaBackup-FaultLab'){throw 'RepositoryRoot yalnız YazmaBackup-FaultLab isimli izole lab yolu olabilir.'}
$missing=Join-Path $RepositoryRoot ('missing-'+[Guid]::NewGuid().ToString('N'))
if(Test-Path $missing){throw 'Fault hedefi mevcut olmamalıdır.'}
$headers=@{Authorization="Bearer $AccessToken"}
for($i=1;$i -le 3;$i++){
  $body=@{policyId=$PolicyId.ToString('D');repositoryRoot=$missing;repositoryId=$RepositoryId}|ConvertTo-Json
  $headers['X-YazmaBackup-Idempotency-Key']="fault-$($RepositoryId)-$([Guid]::NewGuid().ToString('N'))"
  Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/admin/agents/$($AgentId.ToString('D'))/repository-health" -Headers $headers -ContentType 'application/json' -Body $body|Out-Null
}
Write-Host 'Üç izole repository-health arızası kuyruğa alındı.'
Write-Host 'Agent komutları işledikten sonra LIST_AGENTS.ps1 ile RepositoryCircuits alanında devrenin açıldığını doğrulayın.'
Write-Host 'Bu script üretim verisi silmez, değiştirmez veya mevcut repository yoluna yazmaz.'
