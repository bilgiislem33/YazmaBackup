param([switch]$SkipInstall,[switch]$SkipAudit)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$front=Join-Path $root 'src\YazmaBackup.Frontend'
$lock=Join-Path $front 'package-lock.json'
if(-not (Get-Command node -ErrorAction SilentlyContinue)){throw 'Node.js bulunamadı. Node.js LTS kurulu olmalıdır.'}
if(-not (Get-Command npm -ErrorAction SilentlyContinue)){throw 'npm bulunamadı.'}
if(-not (Test-Path $lock)){throw 'R26 production frontend build package-lock.json olmadan çalışmaz. README_BUILD.md bootstrap adımını uygulayın ve lock dosyasını kaynak pakete ekleyin.'}
Push-Location $front
try{
  if(-not $SkipInstall){
    npm ci --no-fund --no-audit
    if($LASTEXITCODE -ne 0){throw 'npm ci başarısız.'}
  }
  if(-not $SkipAudit){
    npm audit --omit=dev --audit-level=high
    if($LASTEXITCODE -ne 0){throw 'Production dependency audit HIGH/CRITICAL güvenlik kapısını geçemedi.'}
  }
  npm run typecheck
  if($LASTEXITCODE -ne 0){throw 'TypeScript typecheck başarısız.'}
  npm run build
  if($LASTEXITCODE -ne 0){throw 'Next.js production build/export başarısız.'}
  $out=Join-Path $front 'out'
  if(-not (Test-Path (Join-Path $out 'index.html'))){throw 'Next.js static export index.html oluşmadı.'}
  $files=Get-ChildItem $out -File -Recurse
  if($files.Count -lt 5){throw 'Next.js export beklenenden az artifact üretti.'}
  $total=($files|Measure-Object Length -Sum).Sum
  if($total -gt 25MB){throw 'Frontend export 25 MB kalite bütçesini aştı.'}
  Write-Host ('PASS: R26 production Next.js export · '+$files.Count+' dosya · '+[math]::Round($total/1MB,2)+' MB')
} finally {Pop-Location}
