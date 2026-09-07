param(
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$Server,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$AccessToken
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$headers = @{ Authorization = "Bearer $AccessToken" }
$result = Invoke-RestMethod -Method Get -Uri "$base/api/v1/admin/pilot/readiness" -Headers $headers
$result | ConvertTo-Json -Depth 8
