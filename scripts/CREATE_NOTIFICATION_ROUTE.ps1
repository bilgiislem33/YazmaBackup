param([Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[Parameter(Mandatory=$true)][string]$Name,[Parameter(Mandatory=$true)][string]$Destination,[ValidateSet('info','warning','critical')][string[]]$Severities=@('critical','warning'))
$ErrorActionPreference='Stop'
$secure=Read-Host 'Webhook HMAC secret (en az 32 karakter)' -AsSecureString
$bstr=[IntPtr]::Zero;$secret=$null
try{
 $bstr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure);$secret=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
 $body=@{name=$Name;destination=$Destination;hmacSecret=$secret;severities=$Severities;enabled=$true}|ConvertTo-Json -Depth 4
 Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/admin/notification-routes" -Headers @{Authorization="Bearer $AccessToken"} -ContentType 'application/json' -Body $body
}finally{$secret=$null;if($bstr-ne[IntPtr]::Zero){[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)}}
