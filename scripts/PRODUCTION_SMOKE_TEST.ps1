param(
  [string]$BaseUri='https://backup.gursoyoto.com.tr',
  [int]$TimeoutSeconds=20
)
$ErrorActionPreference='Stop'
$base=$BaseUri.TrimEnd('/')
$checks=@()
function Add-Check([string]$name,[bool]$ok,[string]$detail){
  $script:checks += [pscustomobject]@{Name=$name;Pass=$ok;Detail=$detail}
  $state=if($ok){'PASS'}else{'FAIL'}
  Write-Host ($state+': '+$name+' · '+$detail)
}
try{
  $root=Invoke-WebRequest -Uri ($base+'/') -UseBasicParsing -TimeoutSec $TimeoutSeconds
  Add-Check 'Public React UI' ($root.StatusCode -eq 200) ('HTTP '+$root.StatusCode)
  Add-Check 'Next static assets' ($root.Content.Contains('/_next/')) 'index.html _next asset reference'
  $corr=$root.Headers['X-YazmaBackup-Correlation-ID']
  Add-Check 'Correlation header' (-not [string]::IsNullOrWhiteSpace($corr)) ('Correlation '+$corr)
  $timing=$root.Headers['Server-Timing']
  Add-Check 'Server timing header' (-not [string]::IsNullOrWhiteSpace($timing)) ($timing)
}catch{
  Add-Check 'Public React UI' $false $_.Exception.Message
}
try{
  Invoke-WebRequest -Uri ($base+'/api/v1/session/me') -UseBasicParsing -TimeoutSec $TimeoutSeconds -ErrorAction Stop | Out-Null
  Add-Check 'Session endpoint' $true 'Authenticated session returned HTTP 2xx'
}catch{
  $code=$_.Exception.Response.StatusCode.value__
  Add-Check 'Session endpoint' ($code -eq 401) ('Expected unauthenticated HTTP '+$code)
}
$failed=@($checks|Where-Object{-not $_.Pass})
Write-Host ''
Write-Host ('Smoke summary: '+($checks.Count-$failed.Count)+'/'+$checks.Count+' PASS')
if($failed.Count -gt 0){throw ('Production smoke test failed: '+($failed.Name -join ', '))}
Write-Host 'PASS: R10.2 production smoke test tamamlandı.'
