$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$front=Join-Path $root 'src\YazmaBackup.Frontend'
$console=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $front 'components\console.tsx')
$css=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $front 'app\globals.css')
$build=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\BUILD_FRONTEND.ps1')
foreach($token in @('CommandPalette','Ctrl+K','ToastViewport','PageSkeleton','aria-live','aria-modal','Ana içeriğe geç')){if(-not $console.Contains($token)){throw ('R10.1 UI invariant eksik: '+$token)}}
foreach($token in @('prefers-reduced-motion','prefers-contrast','focus-visible')){if(-not $css.Contains($token)){throw ('R10.1 accessibility invariant eksik: '+$token)}}
if(-not (Test-Path (Join-Path $front 'app\error.tsx'))){throw 'Global error boundary eksik.'}
if(-not (Test-Path (Join-Path $front 'app\loading.tsx'))){throw 'Global loading state eksik.'}
foreach($token in @('npm ci','npm audit --omit=dev --audit-level=high','25MB')){if(-not $build.Contains($token)){throw ('R10.1 build gate invariant eksik: '+$token)}}
Write-Host 'PASS: R10.1 Enterprise Frontend Hardening statik kalite kapısı.'
