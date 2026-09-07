param(
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$Server,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$AccessToken,
    [Parameter(Mandatory=$true)][Guid]$AgentId,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SourcePath,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$RepositoryRoot,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$RepositoryId,
    [long]$MinimumFreeBytes = 21474836480
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$headers = @{ Authorization = "Bearer $AccessToken"; 'X-YazmaBackup-Idempotency-Key' = [guid]::NewGuid().ToString() }
$body = @{ sourcePath=$SourcePath; repositoryRoot=$RepositoryRoot; repositoryId=$RepositoryId; requireSnapshot=$true; minimumFreeBytes=$MinimumFreeBytes } | ConvertTo-Json
$queued = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/agents/$AgentId/pilot-readiness-probe" -Headers $headers -ContentType 'application/json' -Body $body
for($i=0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 1
    $status = Invoke-RestMethod -Method Get -Uri "$base/api/v1/admin/commands/$($queued.commandId)" -Headers @{ Authorization = "Bearer $AccessToken" }
    if($status.completed) {
        if(-not $status.succeeded) { if([string]::IsNullOrWhiteSpace([string]$status.error)){ throw 'Pilot readiness probe failed.' } else { throw [string]$status.error } }
        $status.resultJson | ConvertFrom-Json | ConvertTo-Json -Depth 8
        exit 0
    }
}
throw 'Pilot readiness probe timed out.'
