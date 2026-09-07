param(
 [Parameter(Mandatory=$true)][string]$Server,
 [Parameter(Mandatory=$true)][Guid]$AgentId,
 [Parameter(Mandatory=$true)][string]$NodeId,
 [ValidateSet('unknown','online','offline','warning')][string]$NodeStatus='unknown',
 [ValidateSet('unknown','installed','updating','failed','missing')][string]$DeploymentStatus='unknown'
)
$ErrorActionPreference='Stop'
$token=$env:YAZMABACKUP_MESHCENTRAL_INTEGRATION_TOKEN
if([string]::IsNullOrWhiteSpace($token)){throw 'YAZMABACKUP_MESHCENTRAL_INTEGRATION_TOKEN tanımlı değil.'}
$body=@{eventId=[Guid]::NewGuid().ToString('D');agentId=$AgentId.ToString('D');nodeId=$NodeId;nodeStatus=$NodeStatus;deploymentStatus=$DeploymentStatus;reportedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json
try{Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/integrations/meshcentral/status" -Headers @{Authorization="Bearer $token"} -ContentType 'application/json' -Body $body}
finally{$token=$null}
