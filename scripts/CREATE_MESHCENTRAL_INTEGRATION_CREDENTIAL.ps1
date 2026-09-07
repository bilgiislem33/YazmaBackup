param(
 [Parameter(Mandatory=$true)][string]$Server,
 [string]$Username='admin',
 [string]$Name='MeshCentral Status',
 [ValidateRange(1,365)][int]$ValidForDays=90
)
$ErrorActionPreference='Stop'
$secure=Read-Host "YazmaBackup yönetici parolası" -AsSecureString
$bstr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try{$plain=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)}
try{
  $loginBody=@{username=$Username;password=$plain}|ConvertTo-Json
  $login=Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/session/login" -ContentType 'application/json' -Body $loginBody -SessionVariable session
}finally{$plain=$null;$loginBody=$null}
if(-not $login.csrfToken){throw 'CSRF token alınamadı.'}
$body=@{name=$Name;purpose='meshcentral-status';validForDays=$ValidForDays}|ConvertTo-Json
$result=Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/admin/integration-credentials" -WebSession $session -Headers @{'X-YazmaBackup-CSRF'=$login.csrfToken} -ContentType 'application/json' -Body $body
Write-Host 'Bu token yalnız bu kez gösterilir. Güvenli secret store içine kaydedin.'
$result
