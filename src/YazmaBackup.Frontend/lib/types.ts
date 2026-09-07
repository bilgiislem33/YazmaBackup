export type SessionUser={username:string;displayName?:string;roles?:string[]};
export type Dashboard={totalAgents:number;onlineAgents:number;offlineAgents:number;enabledPolicies:number;disabledPolicies:number;pendingCommands:number;failedCommandsLast24Hours:number;lockedAgents:number;generatedAtUtc:string};
export type Health={score:number;grade:string;successfulBackups:number;failedBackups:number;successfulRestoreDrills:number;failedRestoreDrills:number;overduePolicies:number;lockedAgents:number;offlineAgents:number;generatedAtUtc:string};
export type Agent={agentId:string;machineName:string;operatingSystem?:string;agentVersion?:string;lastSeenUtc?:string;protectionStatus?:string;assignedUser?:string|null};
export type Policy={policyId:string;name:string;agentId:string;sourcePath:string;repositoryRoot:string;repositoryId:string;intervalMinutes:number;enabled:boolean;nextRunAtUtc?:string};
export type NasProfile={repositoryId:string;repositoryRoot:string;username:string;passwordConfigured:boolean;version:number;updatedAtUtc:string};
