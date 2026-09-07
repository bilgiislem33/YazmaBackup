param(
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$Server,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$AccessToken,
    [Parameter(Mandatory=$true)][ValidateSet('office-balanced','finance-critical','mobile-laptop','archive-steady')][string]$TemplateId,
    [Parameter(Mandatory=$true)][Guid[]]$AgentIds,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SourcePath,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$RepositoryRoot,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$RepositoryId,
    [string]$PolicyNamePrefix = ''
)
$ErrorActionPreference = 'Stop'
if($AgentIds.Count -lt 1 -or $AgentIds.Count -gt 500) { throw 'AgentIds count must be 1..500.' }
$base = $Server.TrimEnd('/')
$body = @{ agentIds=$AgentIds; sourcePath=$SourcePath; repositoryRoot=$RepositoryRoot; repositoryId=$RepositoryId; policyNamePrefix=$PolicyNamePrefix; requireSnapshot=$true; enabled=$true } | ConvertTo-Json -Depth 5
$result = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/policy-templates/$TemplateId/apply-bulk" -Headers @{ Authorization = "Bearer $AccessToken" } -ContentType 'application/json' -Body $body
Write-Host ("Created {0} policies with template {1}." -f $result.createdCount,$result.templateId) -ForegroundColor Green
$result.policies | Select-Object policyId,name,agentId,sourcePath,intervalMinutes,restoreDrillIntervalDays | Format-Table -AutoSize
