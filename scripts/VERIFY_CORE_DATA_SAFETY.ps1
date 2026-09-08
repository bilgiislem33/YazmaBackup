$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$retention=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Application\RetentionManager.cs')
$abstractions=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Application\Abstractions.cs')
$repository=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Infrastructure\FileSystemBackupRepository.cs')
$hosting=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Hosting\ControlPlaneHosting.cs')
$autonomous=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Endpoints\AutonomousProtectionEndpoints.cs')
$autonomousPolicy=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\AutonomousExecutionPolicy.cs')

foreach($required in @('RetentionPlan','PlanAsync','allBeforeDelete','Retention inventory changed after planning','Global repository inventory is incomplete')){
  if(-not $retention.Contains($required)){throw ('Core retention fail-safe invariant eksik: '+$required)}
}
foreach($required in @('IRetentionSafeRepository','AcquireMutationLeaseAsync','QuarantineManifestAsync','ListQuarantinedManifestsAsync','PurgeQuarantinedManifestAsync','ManifestQuarantinePeriod')){
  if(-not (($abstractions+$retention+$repository).Contains($required))){throw ('Retention quarantine invariant eksik: '+$required)}
}
if(($abstractions -split 'public interface IRetentionSafeRepository')[0] -match 'Delete(Manifest|Chunk)'){
  throw 'Doğrudan manifest/chunk silme API temel repository sözleşmesinden kaldırılmalıdır.'
}
if($repository -notmatch 'candidates\.AddRange\(Directory\.EnumerateFiles\(_quarantineRoot'){
  throw 'Karantina manifest anahtar referans koruması eksik.'
}
foreach($required in @('YAZMABACKUP_ENABLE_AUTONOMOUS_EXECUTION','autonomousExecutionEnabled')){
  if(-not (($hosting+$autonomous+$autonomousPolicy).Contains($required))){throw ('Autonomous opt-in invariant eksik: '+$required)}
}
if($hosting -notmatch 'haRole != "standby" && autonomousExecutionEnabled'){
  throw 'Autonomous executor varsayılan kapalı çalışma sınırı eksik.'
}

$minimums=@{
  'RetentionSafetyTests.cs'=12
  'BackupEngineSafetyTests.cs'=7
  'RestoreEngineSafetyTests.cs'=10
  'RepositoryIntegrityTests.cs'=13
}
foreach($entry in $minimums.GetEnumerator()){
  $file=Get-ChildItem (Join-Path $root 'tests') -Recurse -File -Filter $entry.Key | Select-Object -First 1
  if($null -eq $file){throw ('Core safety test dosyası eksik: '+$entry.Key)}
  $count=@(Select-String -Path $file.FullName -Pattern '^\s*\[(Fact|Theory)\]').Count
  if($count -lt $entry.Value){throw ("Core safety test bütçesi geriledi: $($entry.Key)=$count, minimum=$($entry.Value)")}
}
Write-Host 'PASS: Core Data Safety retention lease + quarantine + preflight + autonomous opt-in + kritik test bütçesi.'
