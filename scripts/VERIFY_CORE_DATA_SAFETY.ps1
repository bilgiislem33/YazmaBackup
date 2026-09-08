$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$retention=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Application\RetentionManager.cs')
$hosting=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Hosting\ControlPlaneHosting.cs')
$autonomous=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.ControlPlane\Endpoints\AutonomousProtectionEndpoints.cs')

foreach($required in @('RetentionPlan','PlanAsync','allBeforeDelete','Retention inventory changed after planning','Global repository inventory is incomplete')){
  if(-not $retention.Contains($required)){throw ('Core retention fail-safe invariant eksik: '+$required)}
}
foreach($required in @('YAZMABACKUP_ENABLE_AUTONOMOUS_EXECUTION','autonomousExecutionEnabled')){
  if(-not (($hosting+$autonomous).Contains($required))){throw ('Autonomous opt-in invariant eksik: '+$required)}
}
if($hosting -notmatch 'haRole != "standby" && autonomousExecutionEnabled'){
  throw 'Autonomous executor varsayılan kapalı çalışma sınırı eksik.'
}

$minimums=@{
  'RetentionSafetyTests.cs'=11
  'BackupEngineSafetyTests.cs'=7
  'RestoreEngineSafetyTests.cs'=10
  'RepositoryIntegrityTests.cs'=7
}
foreach($entry in $minimums.GetEnumerator()){
  $file=Get-ChildItem (Join-Path $root 'tests') -Recurse -File -Filter $entry.Key | Select-Object -First 1
  if($null -eq $file){throw ('Core safety test dosyası eksik: '+$entry.Key)}
  $count=@(Select-String -Path $file.FullName -Pattern '^\s*\[(Fact|Theory)\]').Count
  if($count -lt $entry.Value){throw ("Core safety test bütçesi geriledi: $($entry.Key)=$count, minimum=$($entry.Value)")}
}
Write-Host 'PASS: Core Data Safety retention preflight + autonomous opt-in + kritik test bütçesi.'
