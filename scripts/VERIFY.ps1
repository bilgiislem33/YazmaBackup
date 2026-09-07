$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host '[1/23] .NET 10 SDK'
    $dotnet = Get-Command dotnet -ErrorAction Stop
    $sdk = (& $dotnet.Source --version).Trim()
    if (-not $sdk.StartsWith('10.')) { throw ".NET 10 SDK gerekli. Bulunan=$sdk" }

    Write-Host '[2/23] Teknik borç / eski aktif sürüm kalıntısı'
    $sourceFiles = Get-ChildItem .\src,.\scripts -Recurse -File -Include *.cs,*.csproj,*.ps1 | Where-Object { $_.Name -ne 'VERIFY.ps1' -and $_.FullName -notmatch '[\\/](bin|obj|dist|artifacts|publish)[\\/]' }
    $hits = $sourceFiles | Select-String -Pattern @('TODO:','FIXME:','HACK:','NotImplementedException') -SimpleMatch
    if ($hits) { $hits | ForEach-Object { Write-Host $_ }; throw 'Teknik borç işareti bulundu.' }
    $old = $sourceFiles | Select-String -Pattern @('0.1.0','0.2.0','0.3.0','0.4.0','0.5.0','0.5.1','0.6.0','0.7.0','0.8.0','0.9.0','1.0.0') -SimpleMatch
    if ($old) { $old | ForEach-Object { Write-Host $_ }; throw 'Aktif kaynak/script ağacında eski release sürümü bulundu.' }
    $invalidDataInheritance = Get-ChildItem .\src -Recurse -File -Filter *.cs | Select-String -Pattern ': InvalidDataException' -SimpleMatch
    if ($invalidDataInheritance) { $invalidDataInheritance | ForEach-Object { Write-Host $_ }; throw 'InvalidDataException sealed olduğundan özel exception tabanı olarak kullanılamaz.' }

    Write-Host '[3/23] Framework / deterministic build / ProjectReference'
    $props = Get-Content .\Directory.Build.props -Raw
    foreach ($required in @('<TargetFramework>net10.0</TargetFramework>','<LangVersion>14.0</LangVersion>','<TreatWarningsAsErrors>true</TreatWarningsAsErrors>','<Deterministic>true</Deterministic>')) { if ($props -notmatch [Regex]::Escape($required)) { throw "Build invariant eksik: $required" } }
    $approvedPackages = @{ 'Npgsql' = '10.0.0' }
    $packageRefs = @()
    Get-ChildItem .\src -Recurse -File -Filter *.csproj | ForEach-Object {
        [xml]$pkgXml = Get-Content $_.FullName -Raw
        @($pkgXml.Project.ItemGroup.PackageReference) | Where-Object { $_ -and $_.Include } | ForEach-Object {
            $packageRefs += [pscustomobject]@{ Project=$_.PSParentPath; Include=[string]$_.Include; Version=[string]$_.Version }
        }
    }
    foreach ($pkg in $packageRefs) {
        if (-not $approvedPackages.ContainsKey($pkg.Include) -or $approvedPackages[$pkg.Include] -ne $pkg.Version) {
            throw "Doğrulanmamış harici PackageReference bulundu: $($pkg.Include) $($pkg.Version)"
        }
    }
    Get-ChildItem .\src -Recurse -File -Filter *.csproj | ForEach-Object { $dir=$_.DirectoryName; [xml]$xml=Get-Content $_.FullName -Raw; @($xml.Project.ItemGroup.ProjectReference) | Where-Object { $_ -and $_.Include } | ForEach-Object { if (-not (Test-Path (Join-Path $dir $_.Include))) { throw "Eksik ProjectReference: $($_.Include)" } } }

    Write-Host '[4/23] Data Plane güvenlik invariantları'
    $repo=Get-Content .\src\YazmaBackup.Infrastructure\FileSystemBackupRepository.cs -Raw
    $crypto=Get-Content .\src\YazmaBackup.Infrastructure\RepositoryCryptoContext.cs -Raw
    $worker=Get-Content .\src\YazmaBackup.Agent\AgentWorker.cs -Raw
    $keyExchange=Get-Content .\src\YazmaBackup.Agent\AgentKeyExchangeStore.cs -Raw
    $transferTracker=Get-Content .\src\YazmaBackup.Agent\TransferActivityTracker.cs -Raw
    $abstractions=Get-Content .\src\YazmaBackup.Application\Abstractions.cs -Raw
    $guard=Get-Content .\src\YazmaBackup.Infrastructure\RansomwareProtectionGuard.cs -Raw
    foreach($required in @('repository.meta','EnsurePlaintextAllowedForLegacyMigration','GetReferencedEncryptionKeyIdsAsync','Append-only manifest violation')){if($repo -notmatch [Regex]::Escape($required)){throw "Repository invariant eksik: $required"}}
    if($crypto -notmatch 'AesGcm'){throw 'AES-GCM eksik.'}
    if($keyExchange -notmatch 'RSAEncryptionPadding\.OaepSHA256'){throw 'Agent key-wrap OAEP-SHA256 akışı eksik.'}
    if($worker -notmatch 'RestoreDrill'){throw 'Agent restore-drill akışı eksik.'}
    foreach($required in @('live-transfer-telemetry-v1','pilot-readiness-probe-v1','PublishTransferTelemetryLoopAsync','transferObserver: transferTracker','ListRestorePoints','PilotReadinessProbe')){if($worker -notmatch [Regex]::Escape($required)){throw "Agent UX/telemetry invariant eksik: $required"}}
    foreach($required in @('ITransferObserver','OnBytesWritten')){if(($transferTracker+$abstractions+$repo) -notmatch [Regex]::Escape($required)){throw "Repository write telemetry invariant eksik: $required"}}
    foreach($required in @('ExtensionChangeRatioThreshold','HighEntropyRatioThreshold','EstimateEntropyAsync','ComputeSha256Async')){if($guard -notmatch $required){throw "Ransomware guard invariant eksik: $required"}}

    Write-Host '[5/23] Cookie / CSRF / RBAC / privilege boundary'
    $program=Get-Content .\src\YazmaBackup.ControlPlane\Program.cs -Raw
    $mgmt=Get-Content .\src\YazmaBackup.ControlPlane\ManagementSecurity.cs -Raw
    $state=Get-Content .\src\YazmaBackup.ControlPlane\StateStore.cs -Raw
    foreach($required in @('CookieAuthenticationDefaults.AuthenticationScheme','SameSiteMode.Strict','ValidCsrf','RequireRateLimiting("login")','IsInteractiveAdministrator','YAZMABACKUP_ENABLE_LEGACY_ADMIN_KEY')){if(($program+$mgmt) -notmatch [Regex]::Escape($required)){throw "Management invariant eksik: $required"}}
    if($program -notmatch 'request.Roles.Contains\(ManagementRoles.Administrator' -or $program -notmatch 'target.Roles.Contains\(ManagementRoles.Administrator'){throw 'Security Administrator → Administrator privilege boundary eksik.'}
    if($state -notmatch 'MaxFailedLogins = 5' -or $state -notmatch 'last enabled administrator cannot be disabled'){throw 'Account lockout/last-admin koruması eksik.'}

    Write-Host '[6/23] OIDC Code+PKCE / RS256 doğrulama'
    $oidc=Get-Content .\src\YazmaBackup.ControlPlane\OidcSecurity.cs -Raw
    foreach($required in @('code_challenge_method','S256','RS256','JWKS','nonce','azp','rsa.KeySize < 2048','aud','exp','nbf')){if($oidc -notmatch [Regex]::Escape($required)){throw "OIDC invariant eksik: $required"}}
    if($oidc -notmatch 'GetRequiredString\(root, "iss"' -or $oidc -notmatch 'GetRequiredString\(root, "sub"'){throw 'OIDC issuer/subject claim binding eksik.'}
    if($oidc -notmatch 'SHA256\.HashData\(Encoding\.UTF8\.GetBytes\(issuer \+ "\\n" \+ subject\)\)'){throw 'OIDC issuer/subject-derived fallback identity hash eksik.'}
    if($oidc -notmatch 'cannot grant an unknown role or the local administrator role'){throw 'OIDC Administrator escalation block eksik.'}

    Write-Host '[7/23] Management API Token / break-glass'
    foreach($required in @('CreateManagementApiTokenAsync','AuthenticateManagementApiTokenAsync','RevokeManagementApiTokenAsync','CreateBreakGlassRecoveryCodesAsync','RedeemBreakGlassRecoveryCodeAsync')){if($state -notmatch $required){throw "Management credential invariant eksik: $required"}}
    if($state -notmatch '"ybmt_"' -or $state -notmatch '"YBRC-"' -or $state -notmatch 'HashToken\(plaintext\)'){throw 'Token/code hash modeli eksik.'}
    if($program -notmatch 'IsInteractiveAdministrator\(http.User\)'){throw 'Interaktif Administrator issuance gate eksik.'}

    Write-Host '[8/23] Alarm Merkezi / leadership lease / Production Fabric'
    $sentinel=Get-Content .\src\YazmaBackup.ControlPlane\OperationalSentinelService.cs -Raw
    $scheduler=Get-Content .\src\YazmaBackup.ControlPlane\PolicySchedulerService.cs -Raw
    foreach($required in @('protection-lock','offline','UpsertAlarmAsync','ResolveAlarmAsync')){if($sentinel -notmatch [Regex]::Escape($required)){throw "Sentinel invariant eksik: $required"}}
    if($sentinel -notmatch 'AgentCommandType\.RestoreDrill' -or $sentinel -notmatch '"restore-drill"' -or $sentinel -notmatch '\$"agent:\{agent\.AgentId\}:\{category\}-failed"'){throw 'Sentinel restore-drill failure alarm composition invariant eksik.'}
    if($scheduler -notmatch 'TryAcquireOrRenewClusterLeaseAsync\("policy-scheduler"'){throw 'Scheduler leadership lease eksik.'}
    if($state -notmatch 'Epoch' -or $state -notmatch 'TryAcquireOrRenewClusterLeaseAsync'){throw 'Monotonic lease epoch modeli eksik.'}
    $resilience=Get-Content .\src\YazmaBackup.ControlPlane\ResilienceStateStore.cs -Raw
    $orchestrator=Get-Content .\src\YazmaBackup.ControlPlane\ResilienceOrchestratorService.cs -Raw
    $notify=Get-Content .\src\YazmaBackup.ControlPlane\NotificationDispatcherService.cs -Raw
    $healthScanner=Get-Content .\src\YazmaBackup.Agent\RepositoryHealthScanner.cs -Raw
    $fabric=Get-Content .\src\YazmaBackup.ControlPlane\ProductionFabricStateStore.cs -Raw
    $fabricService=Get-Content .\src\YazmaBackup.ControlPlane\ProductionFabricOrchestratorService.cs -Raw
    $circuit=Get-Content .\src\YazmaBackup.Infrastructure\RepositoryCircuitBreaker.cs -Raw
    $evidence=Get-Content .\src\YazmaBackup.ControlPlane\RecoveryEvidenceService.cs -Raw
    foreach($required in @('RepositoryHealthScan','EstimatedDaysToFull','RecoveryPlanRecord','MaxParallelAgents','RtoTargetMinutes','MeshCentralLinkRecord','PrepareNotificationDeliveriesAsync')){if(($resilience+$orchestrator+$healthScanner+$state) -notmatch [Regex]::Escape($required)){throw "Resilience invariant eksik: $required"}}
    foreach($required in @('YAZMABACKUP_NOTIFICATION_ALLOWED_HOSTS','X-YazmaBackup-Signature','HMACSHA256','YazmaBackup.NotificationHmac.v1')){if($notify -notmatch [Regex]::Escape($required)){throw "Notification invariant eksik: $required"}}
    foreach($required in @('Backoff','TimeSpan.FromMinutes(60)','ConsecutiveFailures','FileOptions.WriteThrough')){if($circuit -notmatch [Regex]::Escape($required)){throw "Repository circuit invariant eksik: $required"}}
    foreach($required in @('RecoveryRunbookRecord','AdvanceProductionFabricAsync','meshcentral-status','ybit_','FixedTimeEquals','Recovery runbook RTO budget exceeded')){if($fabric -notmatch [Regex]::Escape($required)){throw "Production Fabric state invariant eksik: $required"}}
    if($fabricService -notmatch 'production-fabric-orchestrator' -or $fabricService -notmatch 'recovery-runbook'){throw 'Production Fabric fenced orchestrator eksik.'}
    foreach($required in @('YazmaBackup.RecoveryEvidenceSigningKey.v1','ECDsa.Create','nistP256','SignHash','ExportSubjectPublicKeyInfoPem')){if($evidence -notmatch [Regex]::Escape($required)){throw "Recovery evidence signing invariant eksik: $required"}}

    Write-Host '[9/23] React Web UI / CSP / güvenli frontend'
    $frontendConsole=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
    $frontendLayout=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\app\layout.tsx')
    $frontendPackage=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\package.json')
    if($frontendLayout -notmatch 'lang="tr"'){throw 'React Türkçe UI bildirimi eksik.'}
    foreach($feature in @('DashboardPage','AgentsPage','OperationsPage','PoliciesPage','RestorePage','RecoveryPage','StoragePage','AlarmsPage','MeshPage','UsersPage','SecurityPage','AuditPage','SettingsPage')){
        if(-not $frontendConsole.Contains($feature)){throw "React UI modülü eksik: $feature"}
    }
    if($frontendConsole -match '\bdangerouslySetInnerHTML\b' -or $frontendConsole -match '\beval\s*\(' -or $frontendConsole -match 'new\s+Function\s*\('){throw 'Riskli React DOM/code execution kalıbı.'}
    if(-not $frontendPackage.Contains('"next"') -or -not $frontendPackage.Contains('"react"')){throw 'React/Next frontend dependency invariant eksik.'}
    foreach($feature in @('/api/v1/agent/transfer-telemetry','/transfer-telemetry','/backup-history','/restore-points','/deep-validation','/granular-restore','/restore-sandbox','/self-healing/diagnose','/self-healing/apply')){
        if($program -notmatch [Regex]::Escape($feature)){throw "Control Plane UX API invariant eksik: $feature"}
    }
    $telemetryRegistry=Get-Content .\src\YazmaBackup.ControlPlane\TransferTelemetryRegistry.cs -Raw
    foreach($feature in @('MaxHistoryBuckets','_aggregateHistory','TransferTelemetrySeriesPointDto','SafeSum')){if($telemetryRegistry -notmatch [Regex]::Escape($feature)){throw "Bounded live telemetry invariant eksik: $feature"}}
    if($program -notmatch "script-src 'self'" -or $program -notmatch "frame-ancestors 'none'"){throw 'CSP header eksik.'}

    Write-Host '[10/23] PostgreSQL migration zinciri / drift'
    $migrations=@(Get-ChildItem .\database\postgresql -File -Filter '*.sql' | Sort-Object Name)
    if($migrations.Count -ne 8){throw "Beklenen 8 PostgreSQL migration yerine $($migrations.Count) bulundu."}
    foreach($m in $migrations){$sql=Get-Content $m.FullName -Raw;if($sql -notmatch 'BEGIN;' -or $sql -notmatch 'COMMIT;'){throw "Transaction sınırı eksik: $($m.Name)"};if($sql -match '(?i)DROP\s+TABLE|TRUNCATE\s+TABLE'){throw "Destructive migration kalıbı: $($m.Name)"}}
    $pg4=Get-Content .\database\postgresql\004_enterprise_control_plane.sql -Raw
    foreach($table in @('management_api_tokens','break_glass_recovery_codes','external_identities','alarms','cluster_leases','control_plane_state_snapshots')){if($pg4 -notmatch "CREATE TABLE IF NOT EXISTS $table"){throw "PG v4 tablo eksik: $table"}}
    $pg5=Get-Content .\database\postgresql\005_enterprise_resilience_plane.sql -Raw
    foreach($table in @('repository_health_records','recovery_plans','recovery_runs','recovery_run_targets','meshcentral_links','notification_routes','notification_deliveries')){if($pg5 -notmatch "CREATE TABLE IF NOT EXISTS $table"){throw "PG v5 tablo eksik: $table"}}
    foreach($required in @('yazmabackup_try_acquire_cluster_lease','epoch + 1','ON CONFLICT (lease_name) DO UPDATE')){if($pg5 -notmatch [Regex]::Escape($required)){throw "PG fenced lease invariant eksik: $required"}}
    $pg6=Get-Content .\database\postgresql\006_production_fabric.sql -Raw
    foreach($table in @('recovery_runbooks','recovery_runbook_runs','integration_credentials','meshcentral_sync_events','repository_circuit_observations','recovery_evidence_exports')){if($pg6 -notmatch "CREATE TABLE IF NOT EXISTS $table"){throw "PG v6 tablo eksik: $table"}}
    $pg7=Get-Content .\database\postgresql\007_validation_self_healing_fabric.sql -Raw
    foreach($table in @('validation_runs','self_healing_events','restore_sandbox_runs')){if($pg7 -notmatch "CREATE TABLE IF NOT EXISTS $table"){throw "PG v7 tablo eksik: $table"}}
    $pg8=Get-Content .\database\postgresql\008_meshcentral_fleet_connector.sql -Raw
    foreach($table in @('meshcentral_connectors','meshcentral_inventory_devices','meshcentral_deployments')){if($pg8 -notmatch "CREATE TABLE IF NOT EXISTS $table"){throw "PG v8 tablo eksik: $table"}}
    $initPg=Get-Content .\scripts\INIT_POSTGRESQL.ps1 -Raw
    if($initPg -notmatch 'MIGRATION DRIFT' -or $initPg -notmatch 'Get-FileHash'){throw 'PostgreSQL checksum drift kapısı eksik.'}

    Write-Host '[11/23] State schema 11 / future downgrade block'
    if($state -notmatch 'SchemaVersion = "11"' -or $state -notmatch 'schemaVersion > 11'){throw 'Control Plane state schema 11 invariant eksik.'}
    foreach($collection in @('ManagementApiTokens','BreakGlassRecoveryCodes','ExternalIdentities','Alarms','ClusterLeases','RepositoryHealth','RecoveryPlans','RecoveryRuns','MeshCentralLinks','NotificationRoutes','NotificationDeliveries','RecoveryRunbooks','RecoveryRunbookRuns','IntegrationCredentials','MeshCentralSyncEvents','MeshCentralConnectors','MeshCentralInventoryDevices','MeshCentralDeployments')){if($state -notmatch $collection){throw "State collection eksik: $collection"}}
    if($state -notmatch 'UnsupportedStateSchemaException'){throw 'Future schema downgrade block eksik.'}
    if($state -match 'UnsupportedStateSchemaException\(string message\)\s*:\s*InvalidDataException'){throw 'UnsupportedStateSchemaException sealed InvalidDataException türünden türetilemez.'}
    if($state -notmatch 'UnsupportedStateSchemaException\(string message\)\s*:\s*Exception\(message\)'){throw 'Future schema exception tabanı beklenen Exception değil.'}
    if($state -match 'ex is not UnsupportedStateSchemaException'){throw 'Future schema exception catch filtresinde artık geçersiz tip deseni bulunuyor.'}

    Write-Host '[12/23] Enterprise Pilot Center invariantları'
    $pilotService=Get-Content .\src\YazmaBackup.ControlPlane\PilotReadinessService.cs -Raw
    $pilotProbe=Get-Content .\src\YazmaBackup.Agent\PilotReadinessProbe.cs -Raw
    $pilotContracts=Get-Content .\src\YazmaBackup.Contracts\PilotContracts.cs -Raw
    foreach($required in @('office-balanced','finance-critical','mobile-laptop','archive-steady','BuildSummaryAsync')){if($pilotService -notmatch [Regex]::Escape($required)){throw "Pilot template/readiness invariant eksik: $required"}}
    foreach($required in @('repository.write','repository.capacity','repository.key','source.ntfs','source.vss','FileOptions.WriteThrough')){if($pilotProbe -notmatch [Regex]::Escape($required)){throw "Agent pilot probe invariant eksik: $required"}}
    foreach($required in @('PilotReadinessProbePayload','PilotReadinessProbeResultDto','BulkApplyPolicyTemplateRequest')){if($pilotContracts -notmatch [Regex]::Escape($required)){throw "Pilot contract invariant eksik: $required"}}
    if($state -notmatch 'CreateBackupPoliciesAsync'){throw 'Atomic bulk policy store invariant eksik.'}
    if($program -notmatch 'apply-bulk' -or $program -notmatch 'pilot-readiness-probe'){throw 'Pilot API endpoint invariant eksik.'}
    if(-not $frontendConsole.Contains('PilotCenterPage') -or -not $frontendConsole.Contains('Kurulum & Pilot Merkezi') -or -not $frontendConsole.Contains('/api/v1/admin/pilot/readiness') -or -not $frontendConsole.Contains('/apply-bulk')){throw 'React Pilot Center UI eksik.'}

    Write-Host '[12b/23] Validation / granular restore / self-healing invariantları'
    $validationContracts=Get-Content .\src\YazmaBackup.Contracts\ValidationContracts.cs -Raw
    $deepProbe=Get-Content .\src\YazmaBackup.Agent\DeepValidationProbe.cs -Raw
    $selfHealing=Get-Content .\src\YazmaBackup.Agent\SelfHealingService.cs -Raw
    $restoreEngine=Get-Content .\src\YazmaBackup.Application\RestoreEngine.cs -Raw
    foreach($required in @('DeepValidationProbePayload','GranularRestorePayload','RestoreSandboxPayload','SelfHealingDiagnosePayload','SelfHealingApplyPayload')){if($validationContracts -notmatch [Regex]::Escape($required)){throw "Validation contract eksik: $required"}}
    foreach($required in @('WindowsVssSnapshotProvider','WindowsUsnJournalChangeTracker','FileOptions.WriteThrough','repository.circuit')){if($deepProbe -notmatch [Regex]::Escape($required)){throw "Deep validation invariant eksik: $required"}}
    foreach($required in @('reset-repository-circuit','cleanup-stale-restore-temp','SafeAutoApply','24')){if(($selfHealing+$validationContracts) -notmatch [Regex]::Escape($required)){throw "Self-healing allowlist invariant eksik: $required"}}
    foreach($required in @('RestoreSelectedAsync','RestoreSandboxAsync','NormalizeRelativeSelector','EnsureUnderRoot')){if($restoreEngine -notmatch [Regex]::Escape($required)){throw "Granular/sandbox restore invariant eksik: $required"}}
    if(-not $frontendConsole.Contains('ValidationPage') -or -not $frontendConsole.Contains('Yedek Doğrulama') -or -not $frontendConsole.Contains('pilot-readiness-probe') -or -not $frontendConsole.Contains('deep-validation')){throw 'React Validation Center UI eksik.'}
    $installer=Get-Content .\scripts\INSTALL_AGENT.ps1 -Raw
    foreach($required in @('ManagementAccessToken','ExpectedAgentId','Post-update health check','eski ImagePath')){if($installer -notmatch [Regex]::Escape($required)){throw "Update rollback health invariant eksik: $required"}}
    $selfTestSource=Get-Content .\src\YazmaBackup.SelfTest\Program.cs -Raw
    foreach($required in @('originalStateDirectory','Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", null)','previousStateDirectory','Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", stateRoot)','Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", futureRoot)','Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", previousStateDirectory)','schema 5 migration loads exactly one legacy policy from isolated test state')){if($selfTestSource -notmatch [Regex]::Escape($required)){throw "SelfTest state izolasyon invariant eksik: $required"}}

    Write-Host '[12c/23] MeshCentral Fleet Connector invariantları'
    $meshService=Get-Content .\src\YazmaBackup.ControlPlane\MeshCentralConnectorService.cs -Raw
    $meshStore=Get-Content .\src\YazmaBackup.ControlPlane\MeshCentralFleetStateStore.cs -Raw
    $meshContracts=Get-Content .\src\YazmaBackup.Contracts\MeshCentralFleetContracts.cs -Raw
    $meshBridge=Get-Content .\src\YazmaBackup.ControlPlane\tools\meshctrl-bridge.js -Raw
    foreach($required in @('ServerInfo','ListDevices','RunCommand','--loginuser','--run','ProtectCredential','ResolveMeshCtrlPath','ToWebSocketBase','rname','ambiguous','NVM_SYMLINK','ContainsMeshCtrlFailure')){if($meshService -notmatch [Regex]::Escape($required)){throw "MeshCentral connector invariant eksik: $required"}}
    if($meshService -match '"--cmd"'){throw 'MeshCentral RunCommand eski --cmd parametresi hâlâ aktif.'}
    $meshDeployWorker=Get-Content .\src\YazmaBackup.ControlPlane\MeshCentralDeploymentHostedService.cs -Raw
    foreach($required in @('BackgroundService','Channel<Guid>','YAZMABACKUP_DEPLOYMENT_SUCCESS','dispatching','TimeSpan.FromMinutes(5)','RecoverQueuedAsync')){if($meshDeployWorker -notmatch [Regex]::Escape($required)){throw "MeshCentral background deployment invariant eksik: $required"}}
    if($program -notmatch 'Results.Accepted\(value: results\)' -or $program -notmatch 'ProtectKeysWithDpapi'){throw 'R4 async deployment veya DPAPI key protection invariant eksik.'}
    foreach($required in @('ExecutionPolicy Bypass','INSTALL_AGENT.ps1 failed. ExitCode=','meshcentral-install-','YazmaBackupAgent service is not Running','agent-access-token.dpapi','Agent enrollment evidence was not created within 90 seconds')){if($program -notmatch [Regex]::Escape($required)){throw "R5 remote installer evidence invariant eksik: $required"}}
    foreach($required in @('Start-Sleep -Seconds 5','service-health-','SC_QUERY=','Service Control Manager','Tanılama:')){if($installer -notmatch [Regex]::Escape($required)){throw "R5 installer diagnostics invariant eksik: $required"}}
    foreach($required in @('New-Service -Name $serviceName','DelayedAutoStart','Windows servisi oluşturulamadı (New-Service, 5 deneme)','ImagePath doğrulamasını geçemedi','OutputEncoding')){if($installer -notmatch [Regex]::Escape($required)){throw "R5.1 service creation reliability invariant eksik: $required"}}
    foreach($required in @('Repair-YazmaBackupProgramDataAcl','Set-PrivateAclOnRoot','ProgramData root ACL reset','ProgramData root private ACL grant','ProgramData descendant emergency grant','AllowNonZero','Critical identity private ACL','create/write/read/delete preflight','ProgramData ACL preflight başarısız','Yarım kalmış Agent identity/token çifti temizlendi','takeown.exe','/reset','/setowner')){if($installer -notmatch [Regex]::Escape($required)){throw "R5.4 ProgramData ACL recovery invariant eksik: $required"}}

    $programCs = Get-Content (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs') -Raw
    foreach($required in @('YazmaBackupDeployLogs','tempLog','fallbackLog','canonicalLog','Copy-Item -LiteralPath $tempLog')){if($programCs -notmatch [Regex]::Escape($required)){throw "R5.5 bootstrap-safe logging invariant eksik: $required"}}
    foreach($required in @('System.Diagnostics.ProcessStartInfo','RedirectStandardOutput','RedirectStandardError','ReadToEndAsync','NativeCommandError','ProgramData early root ownership recovery','R5.6 ProgramData ACL recovery')){if($installer -notmatch [Regex]::Escape($required)){throw "R5.6 native ACL recovery invariant eksik: $required"}}
    if($installer -match '&\s+(?:takeown|icacls)\.exe[^\r\n]*2>&1'){throw 'R5.6 native ACL recovery kapısı: doğrudan native stderr redirection kalıntısı bulundu.'}
    foreach($required in @('Invoke-ServiceWmiChange','Win32_Service.Change','GetMethodParameters(''Change'')','New-GmsaServiceWmi','Win32_Service','GetMethodParameters(''Create'')','R5.7 Windows service configuration WMI/.NET path','Set-ItemProperty -LiteralPath $serviceRegistryPath -Name Description')){if($installer -notmatch [Regex]::Escape($required)){throw "R5.7 deterministic service configuration invariant eksik: $required"}}
    if($installer -match '(?im)^\s*&?\s*sc\.exe\s+(?:config|create|delete)\b'){throw 'R5.7 service gate: sc.exe config/create/delete install/update/rollback yolunda kullanılamaz.'}

    $program = Get-Content -LiteralPath (Join-Path $root 'src/YazmaBackup.ControlPlane/Program.cs') -Raw
    $worker = Get-Content -LiteralPath (Join-Path $root 'src/YazmaBackup.ControlPlane/MeshCentralDeploymentHostedService.cs') -Raw
    foreach($required in @('x.AgentId == auth.Value.AgentId','Authenticated heartbeat completed exact deployment binding','exactDeployments.Length == 0 && hostCandidates.Length == 1','Agent heartbeat reconciled deployment immediately')){if($program -notmatch [Regex]::Escape($required)){throw "Exact deployment heartbeat reconciliation invariant eksik: $required"}}
    foreach($required in @('TrackEnrollment','TryConsumeEnrollment','MeshEnrollmentCorrelation')){if((Get-Content .\src\YazmaBackup.ControlPlane\MeshCentralBootstrapTicketService.cs -Raw) -notmatch [Regex]::Escape($required)){throw "Exact deployment enrollment correlation invariant eksik: $required"}}
    $agentWorker = Get-Content .\src\YazmaBackup.Agent\AgentWorker.cs -Raw
    foreach($required in @('TryRecoverAuthenticationAsync','EnrollWithTokenAsync','agent-authentication-recovered')){if($agentWorker -notmatch [Regex]::Escape($required)){throw "Agent self-healing enrollment invariant eksik: $required"}}
    foreach($required in @('R5.8 fallback','deployments.Any(x => x.Status == "installing")','SynchronizeAsync(connector, ct)')){if($worker -notmatch [Regex]::Escape($required)){throw "R5.8 fallback reconciliation invariant eksik: $required"}}
    $runServer=Get-Content .\scripts\RUN_SERVER.ps1 -Raw
    if($runServer -notmatch 'src\\YazmaBackup.ControlPlane\\.local' -or $runServer -notmatch 'YAZMABACKUP_STATE_DIR = \$stateDir'){throw 'RUN_SERVER state path hizalama invariant eksik.'}
    foreach($required in @('YAZMABACKUP_AGENT_PACKAGE_ZIP','YazmaBackupAgent_*_win-x64.zip','Agent deployment paketi otomatik bulundu')){if($runServer -notmatch [Regex]::Escape($required)){throw "R5 Agent ZIP auto-discovery invariant eksik: $required"}}
    foreach($required in @('MeshCentralConnectors','MeshCentralInventoryDevices','MeshCentralDeployments','GetMeshCentralFleetSummaryAsync')){if(($meshStore+$state) -notmatch [Regex]::Escape($required)){throw "MeshCentral fleet store invariant eksik: $required"}}
    foreach($required in @('--loginpass','YAZMABACKUP_MESHCTRL_CREDENTIAL','delete process.env.YAZMABACKUP_MESHCTRL_CREDENTIAL')){if($meshBridge -notmatch [Regex]::Escape($required)){throw "MeshCentral bridge invariant eksik: $required"}}
    foreach($required in @('SaveMeshCentralConnectorRequest','DeployMeshCentralAgentsRequest','MeshCentralFleetSummaryDto')){if($meshContracts -notmatch [Regex]::Escape($required)){throw "MeshCentral fleet contract eksik: $required"}}
    foreach($required in @('/meshcentral/connector','/meshcentral/test','/meshcentral/synchronize','/meshcentral/fleet','/meshcentral/deploy','/api/v1/bootstrap/meshcentral/','/api/v1/bootstrap/package/')){if($program -notmatch [Regex]::Escape($required)){throw "MeshCentral fleet API eksik: $required"}}
    foreach($required in @('MeshPage','/api/v1/admin/meshcentral/connector','/api/v1/admin/meshcentral/fleet','/api/v1/admin/meshcentral/deploy')){if(-not $frontendConsole.Contains($required)){throw "React MeshCentral fleet UI eksik: $required"}}
    if(-not $frontendConsole.Contains('action(kind:"test"|"synchronize")') -or -not $frontendConsole.Contains('"/api/v1/admin/meshcentral/"+kind')){throw 'React MeshCentral test/synchronize action invariant eksik.'}

    Write-Host '[12d/23] R5.19 Functional Command Center invariantları'
    $contracts = Get-Content .\src\YazmaBackup.Contracts\Contracts.cs -Raw
    $enterpriseContracts = Get-Content .\src\YazmaBackup.Contracts\EnterpriseControlContracts.cs -Raw
    $backupEngine = Get-Content .\src\YazmaBackup.Application\BackupEngine.cs -Raw
    $ransomwareGuard = Get-Content .\src\YazmaBackup.Infrastructure\RansomwareProtectionGuard.cs -Raw
    foreach($required in @('IgnoreInaccessible = true','AttributesToSkip = FileAttributes.ReparsePoint')){if($backupEngine -notmatch [Regex]::Escape($required) -or $ransomwareGuard -notmatch [Regex]::Escape($required)){throw "Windows safe traversal invariant eksik: $required"}}
    foreach($required in @('Stage = null','LogicalBytesProcessed','LogicalBytesTotal','FilesProcessed','FilesTotal','ListRestoreEntriesPayload','ListRestoreEntriesResultDto')){if($contracts -notmatch [Regex]::Escape($required)){throw "R5.19 telemetry/restore explorer contract eksik: $required"}}
    foreach($required in @('UpdateAlarmWorkflowRequest','BulkAlarmActionRequest','AssignedTo','OperatorNote','DueAtUtc')){if(($enterpriseContracts + (Get-Content .\src\YazmaBackup.Domain\EnterpriseControlModels.cs -Raw)) -notmatch [Regex]::Escape($required)){throw "R5.19 alarm workflow contract eksik: $required"}}
    foreach($required in @('restore-entries','/alarms/bulk','/alarms/{alarmId:guid}/workflow')){if($program -notmatch [Regex]::Escape($required)){throw "R5.19 API invariant eksik: $required"}}
    foreach($required in @('CommandCenterPage','Device 360','restore-entries','AlarmsPage')){if(-not $frontendConsole.Contains($required)){throw "React Functional UI invariant eksik: $required"}}

    Write-Host '[12e/23] Secure NAS credential / SMB access test invariantları'
    $nasAgent = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Agent\NasCredentialStore.cs')
    $nasScope = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Agent\NasConnectionScope.cs')
    $agentWorker = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Agent\AgentWorker.cs')
    $contracts = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Contracts\Contracts.cs')
    $program = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
    $agentPaths = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Agent\AgentPaths.cs')
    if($nasAgent -notmatch 'MachineSecretStore' -or $nasAgent -notmatch 'NasCredentialDirectory' -or $agentPaths -notmatch 'nas-credentials'){throw 'NAS credential DPAPI store invariant eksik.'}
    if($nasScope -notmatch 'WNetAddConnection2' -or $nasScope -notmatch 'WNetCancelConnection2'){throw 'NAS SMB connection scope invariant eksik.'}
    if($agentWorker -notmatch 'ProvisionNasCredential' -or $agentWorker -notmatch 'TestNasAccess'){throw 'Agent secure NAS command invariant eksik.'}
    if($contracts -notmatch 'WrappedNasCredentialPayload' -or $contracts -notmatch 'NasAccessTestResultDto'){throw 'NAS contract invariant eksik.'}
    if($program -notmatch 'RSAEncryptionPadding\.OaepSHA256' -or $program -notmatch '/nas-access-test'){throw 'Control Plane secure NAS transport/test invariant eksik.'}
    if($agentWorker -notmatch 'ProvisionAndTestNasCredential' -or $program -notmatch '/nas-credential/save-and-test'){throw 'NAS deterministic save-and-test invariant eksik.'}

    Write-Host '[12f/23] Backup failure diagnostics / 503 resilience invariantları'
    $agentWorker = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Agent\AgentWorker.cs')
    $program = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
    if($agentWorker -notmatch 'command-failed' -or $agentWorker -notmatch 'control-plane-temporarily-unavailable'){throw 'Agent failure diagnostics/backoff invariant eksik.'}
    if($program -notmatch 'ClassifyBackupError' -or $program -notmatch 'repositoryRoot = request\?\.RepositoryRoot'){throw 'Backup history diagnostic projection invariant eksik.'}

    Write-Host '[12g/23] Automatic repository key bootstrap invariantları'
    $vault = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\RepositoryKeyVault.cs')
    $program = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
    if($vault -notmatch 'IDataProtector' -or $vault -notmatch 'RandomNumberGenerator\.GetBytes\(32\)' -or $vault -notmatch 'repository-key-vault'){throw 'Protected central repository key vault invariant eksik.'}
    if($program -notmatch 'RepositoryKeyVault keyVault' -or $program -notmatch 'ProvisionRepositoryKey' -or $program -notmatch 'queued-before-backup'){throw 'Automatic repository key bootstrap ordering invariant eksik.'}
    if($program -notmatch 'RSAEncryptionPadding\.OaepSHA256'){throw 'Repository key Agent wrapping invariant eksik.'}

    Write-Host '[12h/23] Domainless bilgisayar sahibi metadata invariantları'
    $models = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Domain\Models.cs')
    $contracts = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Contracts\Contracts.cs')
    $store = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\StateStore.cs')
    $program = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
    if($models -notmatch 'string\? AssignedUser = null'){throw 'Agent AssignedUser metadata invariant eksik.'}
    if($contracts -notmatch 'SetAgentAssignedUserRequest'){throw 'AssignedUser contract invariant eksik.'}
    if($store -notmatch 'SetAgentAssignedUserAsync' -or $store -notmatch 'AssignedUser = normalized'){throw 'AssignedUser persistence invariant eksik.'}
    if($program -notmatch '/agents/\{agentId:guid\}/assigned-user'){throw 'AssignedUser API invariant eksik.'}
    if($frontendConsole -notmatch 'assignedUser' -or $frontendConsole -notmatch 'saveOwner'){throw 'React AssignedUser UI invariant eksik.'}

    Write-Host '[12j/23] Zero-touch Agent lifecycle + React UI invariantları'
    $models = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Domain\Models.cs')
    $worker = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Agent\AgentWorker.cs')
    $applier = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.Agent\AgentUpdateApplier.cs')
    $installer = Get-Content -Raw -LiteralPath (Join-Path $root 'scripts\INSTALL_AGENT.ps1')
    $program = Get-Content -Raw -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Program.cs')
    if($models -notmatch 'ApplyStagedAgentUpdate = 23' -or $worker -notmatch 'ApplyStagedAgentUpdate'){throw 'Agent apply-update command invariant eksik.'}
    if($applier -notmatch 'update-ready.json' -or $applier -notmatch 'INSTALL_AGENT.ps1'){throw 'Staged update applier invariant eksik.'}
    if($installer -notmatch '\$inPlaceUpgrade' -or $installer -notmatch 'AgentId/token/config/repository keys/NAS credentials korunacak'){throw 'In-place identity preservation invariant eksik.'}
    if($program -notmatch '/fleet/lifecycle' -or $program -notmatch '/apply-staged-update'){throw 'Fleet lifecycle API invariant eksik.'}
    if(-not $frontendConsole.Contains('LifecyclePage')){throw 'React lifecycle UI invariant eksik.'}

    Write-Host '[12k/23] Legacy UI retirement / React single-source invariantları'
    if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\wwwroot')){throw 'Legacy source wwwroot geri gelmiş.'}
    if(Test-Path (Join-Path $root 'src\YazmaBackup.ControlPlane\legacy-ui')){throw 'legacy-ui geri gelmiş.'}
    if(-not (Test-Path (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx'))){throw 'React console source eksik.'}

    Write-Host '[12n/23] R26 gerçek React/Next.js production cutover'
    & (Join-Path $root 'scripts\VERIFY_REAL_REACT_CUTOVER.ps1')

    Write-Host '[12ab/23] R10.1 enterprise frontend hardening invariantları'
    & (Join-Path $root 'scripts\VERIFY_FRONTEND_HARDENING.ps1')

    Write-Host '[12ac/23] R10.2 production reliability & observability invariantları'
    & (Join-Path $root 'scripts\VERIFY_RELIABILITY.ps1')

    Write-Host '[12ad/23] R10.3 autonomous reliability & self-healing invariantları'
    & (Join-Path $root 'scripts\VERIFY_AUTONOMOUS_RELIABILITY.ps1')

    Write-Host '[12ae/23] R10.4 autonomous remediation orchestrator + canary rollout controller invariantları'
    & (Join-Path $root 'scripts\VERIFY_AUTONOMOUS_ORCHESTRATOR.ps1')
    Write-Host '[12aef/23] R27.3.8 R10.4 behavior-based gate fix'
    & (Join-Path $root 'scripts\VERIFY_R27_3_8_R10_4_GATE_FIX.ps1')

    Write-Host '[12af/23] R10.5 enterprise HA + zero-downtime switchover + DR invariantları'
    & (Join-Path $root 'scripts\VERIFY_HA_DR.ps1')
    Write-Host '[12afg/23] R27.3.9 R10.5 current HA behavior gate'
    & (Join-Path $root 'scripts\VERIFY_R27_3_9_R10_5_GATE_FIX.ps1')

    Write-Host '[12ag/23] R11 distributed state engine + active/active control plane invariantları'
    & (Join-Path $root 'scripts\VERIFY_DISTRIBUTED_STATE.ps1')

    Write-Host '[12ah/23] R12 PostgreSQL transactional state engine + production active/active invariantları'
    & (Join-Path $root 'scripts\VERIFY_TRANSACTIONAL_STATE.ps1')

    Write-Host '[12ai/23] R13 normalized enterprise PostgreSQL schema invariantları'
    & (Join-Path $root 'scripts\VERIFY_NORMALIZED_SCHEMA.ps1')

    Write-Host '[12aj/23] R14 direct SQL read cutover invariantları'
    & (Join-Path $root 'scripts\VERIFY_DIRECT_SQL_READS.ps1')

    Write-Host '[12ak/23] R15 direct SQL mutation core cutover invariantları'
    & (Join-Path $root 'scripts\VERIFY_DIRECT_SQL_MUTATIONS.ps1')

    Write-Host '[12al/23] R16 PostgreSQL command transaction engine invariantları'
    & (Join-Path $root 'scripts\VERIFY_COMMAND_TRANSACTION_ENGINE.ps1')

    Write-Host '[12am/23] R17 multi-aggregate atomic completion invariantları'
    & (Join-Path $root 'scripts\VERIFY_MULTI_AGGREGATE_COMPLETION.ps1')

    Write-Host '[12an/23] R18 Enterprise Backup Intelligence invariantları'
    & (Join-Path $root 'scripts\VERIFY_BACKUP_INTELLIGENCE.ps1')

    Write-Host '[12ao/23] R19 Fleet Autopilot + Restore Readiness invariantları'
    & (Join-Path $root 'scripts\VERIFY_FLEET_AUTOPILOT.ps1')

    Write-Host '[12ap/23] R20 Predictive Protection Engine invariantları'
    & (Join-Path $root 'scripts\VERIFY_PREDICTIVE_PROTECTION.ps1')

    Write-Host '[12aq/23] R21 Closed-Loop Protection Orchestrator invariantları'
    & (Join-Path $root 'scripts\VERIFY_CLOSED_LOOP_PROTECTION.ps1')

    Write-Host '[12ar/23] R22 Enterprise Recovery Fabric invariantları'
    & (Join-Path $root 'scripts\VERIFY_RECOVERY_FABRIC.ps1')

    Write-Host '[12as/23] R23 Business Continuity Command Center invariantları'
    & (Join-Path $root 'scripts\VERIFY_BUSINESS_CONTINUITY.ps1')

    Write-Host '[12at/23] R24 Business Service Graph + Recovery DAG invariantları'
    & (Join-Path $root 'scripts\VERIFY_BUSINESS_SERVICE_GRAPH.ps1')

    Write-Host '[12au/23] R25 DR Execution + PostgreSQL completion fence invariantları'
    & (Join-Path $root 'scripts\VERIFY_R25_DR_EXECUTION_CONSISTENCY.ps1')

    Write-Host '[12av/23] R27 Frontend Experience invariantları'
    & (Join-Path $root 'scripts\VERIFY_R27_FRONTEND_EXPERIENCE.ps1')

    Write-Host '[12aw/23] R27.1 Fleet Galaxy + Device 360 invariantları'
    & (Join-Path $root 'scripts\VERIFY_R27_1_FLEET_GALAXY.ps1')

    Write-Host '[12ax/23] R27.2 Recovery Experience + DR War Room invariantları'
    & (Join-Path $root 'scripts\VERIFY_R27_2_RECOVERY_EXPERIENCE.ps1')

    Write-Host '[12ay/23] R27.3 Ultimate Enterprise Experience invariantları'
    & (Join-Path $root 'scripts\VERIFY_R27_3_ULTIMATE_ENTERPRISE_EXPERIENCE.ps1')

    Write-Host '[12az/23] R27.3.1 dependency / VERIFY root-cause fix'
    & (Join-Path $root 'scripts\VERIFY_R27_3_1_DEPENDENCY_VERIFY_FIX.ps1')

    Write-Host '[12ba/23] R27.3.2 React VERIFY cutover fix'
    & (Join-Path $root 'scripts\VERIFY_R27_3_2_REACT_VERIFY_CUTOVER.ps1')

    Write-Host '[12bb/23] R27.3.3 React Enterprise Pilot Center'
    & (Join-Path $root 'scripts\VERIFY_R27_3_3_PILOT_CENTER.ps1')

    Write-Host '[12bc/23] R27.3.4 MeshCentral React VERIFY fix'
    & (Join-Path $root 'scripts\VERIFY_R27_3_4_MESH_VERIFY_FIX.ps1')

    Write-Host '[12bd/23] R27.3.5 TypeScript production build compatibility'
    & (Join-Path $root 'scripts\VERIFY_R27_3_5_TYPESCRIPT_CUTOVER_FIX.ps1')

    Write-Host '[12be/23] R27.3.6 cumulative production build repair'
    & (Join-Path $root 'scripts\VERIFY_R27_3_6_CUMULATIVE_REPAIR.ps1')

    Write-Host '[12bf/23] R27.3.7 post-R26 React gate audit'
    & (Join-Path $root 'scripts\VERIFY_R27_3_7_REACT_GATE_AUDIT.ps1')

    Write-Host '[13/23] dotnet restore' 
    & $dotnet.Source restore .\YazmaBackup.sln --nologo
    if($LASTEXITCODE -ne 0){throw 'dotnet restore başarısız.'}

    Write-Host '[14/23] Release build / warnings-as-errors'
    & $dotnet.Source build .\YazmaBackup.sln -c Release --no-restore --nologo
    if($LASTEXITCODE -ne 0){throw 'Release build başarısız.'}

    Write-Host '[15/23] Enterprise self-test'
    & $dotnet.Source run --project .\src\YazmaBackup.SelfTest\YazmaBackup.SelfTest.csproj -c Release --no-build
    if($LASTEXITCODE -ne 0){throw 'Self-test başarısız.'}

    Write-Host '[16/23] PowerShell 5.1 syntax'
    $parseErrors=@(); Get-ChildItem .\scripts -File -Filter *.ps1 | ForEach-Object {$tokens=$null;$errors=$null;[void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$errors);if($errors.Count -gt 0){$parseErrors += $errors}}
    if($parseErrors.Count -gt 0){$parseErrors | ForEach-Object {Write-Host $_};throw 'PowerShell syntax hatası.'}
    $bomErrors=@(); Get-ChildItem .\scripts -File -Filter *.ps1 | ForEach-Object {$bytes=[System.IO.File]::ReadAllBytes($_.FullName);if($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF){$bomErrors += $_.Name}}
    if($bomErrors.Count -gt 0){throw "Windows PowerShell 5.1 UTF-8 BOM eksik: $($bomErrors -join ', ')"}

    Write-Host '[17/23] JavaScript syntax'
    $node=Get-Command node -ErrorAction SilentlyContinue
    if($node){& $node.Source --check .\src\YazmaBackup.ControlPlane\tools\meshctrl-bridge.js;if($LASTEXITCODE -ne 0){throw 'MeshCentral bridge JavaScript syntax hatası.'}} else {Write-Warning 'node bulunamadı; JS syntax kapısı Windows VERIFY içinde atlandı.'}

    Write-Host '[18/23] Secret/private-key taraması'
    $secretPattern='(?i)(password|secret|token|keybase64)\s*=\s*["''][^"''$]{8,}["'']'
    $secretHits=Get-ChildItem .\src,.\scripts -Recurse -File -Include *.cs,*.json,*.ps1,*.js | Where-Object {$_.Name -ne 'VERIFY.ps1'} | Select-String -Pattern $secretPattern
    if($secretHits){$secretHits|ForEach-Object{Write-Host $_};throw 'Olası gömülü secret bulundu.'}
    if(Get-ChildItem . -Recurse -File -Include *.pem,*.key | Select-String -Pattern 'BEGIN .*PRIVATE KEY' -ErrorAction SilentlyContinue){throw 'Kaynak ağacında private key bulundu.'}

    Write-Host '[19/23] Doküman sözleşmesi'
    foreach($doc in @('OIDC_SSO.md','ALARM_CENTER.md','CONTROL_PLANE_CLUSTER.md','POSTGRESQL_CUTOVER.md','PROGRESS.md','MESHCENTRAL_DEPLOYMENT.md','RESILIENCE.md','NOTIFICATIONS.md','MESHCENTRAL_INTEGRATION.md','PRODUCTION_FABRIC.md','CIRCUIT_BREAKER.md','RECOVERY_RUNBOOK.md','MESHCENTRAL_STATUS_INGESTION.md','FAULT_INJECTION_PILOT.md','WEB_UI.md','LIVE_TRANSFER_TELEMETRY.md','EASY_RESTORE.md','PILOT_CENTER.md','VALIDATION_FABRIC.md','SELF_HEALING.md','GRANULAR_RESTORE_SANDBOX.md','UPDATE_ROLLBACK_VALIDATION.md')){if(-not(Test-Path (Join-Path '.\docs' $doc))){throw "Doküman eksik: $doc"}}
    $readme=Get-Content .\README.md -Raw
    if($readme -match '-AdminKey\s+\$env:YAZMABACKUP_ADMIN_KEY'){throw 'README eski AdminKey CLI akışını kullanıyor.'}
    if($readme -notmatch '-KeyFile \$key\.keyFile' -or $readme -notmatch 'Management API Token'){throw 'README yeni key/token akışıyla senkron değil.'}

    Write-Host '[20/23] Operasyon scriptleri'
    foreach($script in @('CREATE_MANAGEMENT_API_TOKEN.ps1','CREATE_BREAK_GLASS_CODES.ps1','LIST_ALARMS.ps1','ACK_ALARM.ps1','GET_CLUSTER_STATUS.ps1','PREFLIGHT_POSTGRESQL.ps1','STAGE_JSON_STATE_POSTGRESQL.ps1','MESH_INSTALL_AGENT.ps1','RESTORE_DRILL.ps1','LIST_REPOSITORY_HEALTH.ps1','CREATE_RECOVERY_PLAN.ps1','LIST_RECOVERY_RUNS.ps1','LINK_MESHCENTRAL_NODE.ps1','CREATE_NOTIFICATION_ROUTE.ps1','CREATE_RECOVERY_RUNBOOK.ps1','LIST_RECOVERY_RUNBOOK_RUNS.ps1','EXPORT_RECOVERY_EVIDENCE.ps1','CREATE_MESHCENTRAL_INTEGRATION_CREDENTIAL.ps1','REPORT_MESHCENTRAL_STATUS.ps1','PILOT_FAULT_INJECTION.ps1','PILOT_READINESS.ps1','PILOT_AGENT_PROBE.ps1','BULK_APPLY_POLICY_TEMPLATE.ps1','CREATE_PILOT_ROLLOUT.ps1','DEEP_VALIDATION.ps1','GRANULAR_RESTORE.ps1','RESTORE_SANDBOX.ps1','SELF_HEAL.ps1','WINDOWS_FAULT_MATRIX.ps1')){if(-not(Test-Path (Join-Path '.\scripts' $script))){throw "Script eksik: $script"}}
    $legacyScriptHits=Get-ChildItem .\scripts -File -Filter *.ps1 | Where-Object {$_.Name -ne 'VERIFY.ps1'} | Select-String -Pattern "'X-YazmaBackup-Admin-Key'" -SimpleMatch
    if($legacyScriptHits){throw 'Operasyon scriptlerinde legacy AdminKey header kalıntısı bulundu.'}

    Write-Host '[21/23] Sürüm / proje tutarlılığı'
    $meta=Get-Content .\VERSION.json -Raw | ConvertFrom-Json
    if($meta.version -ne '1.2.0' -or $meta.stateSchema -ne 11 -or $meta.productionReady -ne $false){throw 'VERSION.json invariantı başarısız.'}
    if(-not(Select-String -Path .\src\YazmaBackup.Agent\AgentWorker.cs -Pattern 'public const string AgentVersion = "1.2.0";' -SimpleMatch)){throw 'AgentVersion 1.2.0 değil.'}
    if(-not(Select-String -Path .\src\YazmaBackup.ControlPlane\Program.cs -Pattern 'version = "1.2.0"' -SimpleMatch)){throw 'Health version 1.2.0 değil.'}
    $projects=Get-ChildItem .\src -Recurse -File -Filter *.csproj
    if($projects.Count -ne 8){throw "Beklenen 8 proje yerine $($projects.Count) bulundu."}

    Write-Host '[22/23] Self-test yeni kontrol-düzlemi senaryoları'
    $selfTest=Get-Content .\src\YazmaBackup.SelfTest\Program.cs -Raw
    foreach($required in @('management API token authenticates with scoped role','break-glass recovery code replay is rejected','OIDC identity remains issuer/subject-bound','alarm can be resolved by fingerprint','alarm workflow persists owner note and SLA due time','expired cluster lease transfers ownership with monotonic epoch','schema 5 policy receives safe 7-day restore-drill default','resilience orchestrator schedules repository health and recovery drill commands','recovery plan creates auditable run with target evidence','repository circuit opens after third transient failure','recovery runbook binds ordered step to recovery run evidence','integration credential secret is returned once and only hash is stored','MeshCentral status ingestion is idempotent by event id','recovery evidence ECDSA signature verifies','notification delivery intent remains idempotent for same alarm version','live transfer telemetry registry exposes active transfer','live transfer telemetry keeps bounded aggregate history','repository transfer telemetry counts bytes only after successful repository writes','pilot center exposes curated policy templates','bulk policy creation commits validated policies together','bulk policy validation failure leaves state unchanged','pilot readiness summary returns bounded evidence-based score','granular restore selects only requested paths','restore sandbox verifies complete restore in isolated destination')){if($selfTest -notmatch [Regex]::Escape($required)){throw "Self-test senaryosu eksik: $required"}}

    Write-Host '[23/23] Kaynak SHA-256 manifesti'
    if(Test-Path .\SOURCE_SHA256SUMS.txt){
        $bad=@(); foreach($line in Get-Content .\SOURCE_SHA256SUMS.txt){if([string]::IsNullOrWhiteSpace($line)){continue};$parts=$line -split '  ',2;if($parts.Count -ne 2){$bad+=$line;continue};$file=Join-Path $root $parts[1];if(-not(Test-Path -LiteralPath $file)){$bad+=$parts[1];continue};$actual=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant();if($actual -ne $parts[0]){$bad+=$parts[1]}};if($bad.Count -gt 0){throw "SOURCE_SHA256SUMS doğrulaması başarısız: $($bad -join ', ')"}
    } else {Write-Warning 'SOURCE_SHA256SUMS.txt henüz yok; release mühürlemesinden önce oluşturulmalıdır.'}

    Write-Host 'PASS: YazmaBackup v1.2.0 MeshCentral Fleet Connector kalite kapıları başarılı.' -ForegroundColor Green
}
finally { Pop-Location }
