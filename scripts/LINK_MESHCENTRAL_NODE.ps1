param([Parameter(Mandatory=$true)][string]$Server,[Parameter(Mandatory=$true)][string]$AccessToken,[Parameter(Mandatory=$true)][Guid]$AgentId,[Parameter(Mandatory=$true)][string]$MeshCentralBaseUri,[Parameter(Mandatory=$true)][string]$NodeId)
$ErrorActionPreference='Stop'
$body=@{meshCentralBaseUri=$MeshCentralBaseUri;nodeId=$NodeId}|ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/v1/admin/agents/$($AgentId.ToString('D'))/meshcentral-link" -Headers @{Authorization="Bearer $AccessToken"} -ContentType 'application/json' -Body $body
