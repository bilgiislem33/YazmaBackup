param(
 [Parameter(Mandatory=$true)][string]$Server,
 [Parameter(Mandatory=$true)][string]$AccessToken,
 [Parameter(Mandatory=$true)][Guid]$RunId,
 [Parameter(Mandatory=$true)][string]$OutputFile
)
$ErrorActionPreference='Stop'
$result=Invoke-RestMethod -Method Get -Uri "$($Server.TrimEnd('/'))/api/v1/admin/recovery-runbook-runs/$($RunId.ToString('D'))/evidence" -Headers @{Authorization="Bearer $AccessToken"}
$parent=Split-Path -Parent $OutputFile
if($parent -and -not (Test-Path $parent)){New-Item -ItemType Directory -Path $parent -Force|Out-Null}
$result|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $OutputFile -Encoding UTF8
Write-Host "Recovery evidence yazıldı: $OutputFile"
Write-Host "Payload SHA-256: $($result.payloadSha256)"
Write-Host "İmza: $($result.signatureAlgorithm)"
