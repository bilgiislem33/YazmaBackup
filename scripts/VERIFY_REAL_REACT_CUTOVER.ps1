$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'SOURCE_TEXT.ps1')
$front=Join-Path $root 'src\YazmaBackup.Frontend'
$cp=Join-Path $root 'src\YazmaBackup.ControlPlane'
$proj=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $cp 'YazmaBackup.ControlPlane.csproj')
$program=Get-YazmaBackupControlPlaneSource -Area Endpoints
$build=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\BUILD_FRONTEND.ps1')
$deploy=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\DEPLOY_FRONTEND.ps1')

if(Test-Path (Join-Path $cp 'legacy-ui')){throw 'R26 single-source ihlali: legacy-ui kaynak ağacında kalmış.'}
if(Test-Path (Join-Path $cp 'wwwroot')){throw 'R26 single-source ihlali: source wwwroot bulunuyor. wwwroot yalnız publish artifact olmalıdır.'}
foreach($x in @('BuildProductionReactFrontend','BeforeTargets="ComputeFilesToPublish"','ResolvedFileToPublish','wwwroot\%(RecursiveDir)')){
 if(-not $proj.Contains($x)){throw ('R26 MSBuild/publish integration eksik: '+$x)}
}
if(-not $program.Contains('UseStaticFiles()') -or -not $program.Contains('MapFallbackToFile("index.html")')){
 throw 'ASP.NET React static artifact serving contract eksik.'
}
if(-not $build.Contains('npm ci') -or $build.Contains('npm install --no-fund')){
 throw 'R26 deterministic npm build contract ihlali.'
}
if(-not $deploy.Contains('.yazmabackup-react-production') -or -not $deploy.Contains('Get-FileHash')){
 throw 'R26 publish artifact fingerprint kapısı eksik.'
}
$lock=Join-Path $front 'package-lock.json'
if(-not (Test-Path $lock)){
 throw 'R26 production cutover henüz doğrulanamaz: package-lock.json eksik. src\YazmaBackup.Frontend\README_BUILD.md içindeki tek seferlik bootstrap adımını uygulayın; lock olmadan React cutover PASS verilmez.'
}
& (Join-Path $root 'scripts\BUILD_FRONTEND.ps1') -SkipAudit
if($LASTEXITCODE -ne 0){throw 'R26 gerçek Next.js build başarısız.'}
$out=Join-Path $front 'out'
if(-not (Test-Path (Join-Path $out 'index.html'))){throw 'R26 export index.html yok.'}
$nextChunks=Get-ChildItem $out -File -Recurse | Where-Object {$_.FullName -match '[\\/]_next[\\/]'}
if(-not $nextChunks){throw 'R26 export içinde Next.js runtime/static chunk bulunamadı.'}
Write-Host ('PASS: R26 tek UI kaynağı + gerçek Next.js build/export + MSBuild publish integration · '+$nextChunks.Count+' Next artifact')
