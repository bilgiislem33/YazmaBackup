$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
$badge=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\ui\badge.tsx')
$progress=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\ui\progress.tsx')
foreach($x in @('Building2','Network','function Metric','description?:string','function fmt(','function mins(','function bytes(')){if(-not $ui.Contains($x)){throw ('R27.3.5 TS compatibility invariant eksik: '+$x)}}
if(-not $badge.Contains('variant?:Variant')){throw 'Badge variant compatibility eksik.'}
if(-not $progress.Contains('className?:string')){throw 'Progress className compatibility eksik.'}
Write-Host 'PASS: R27.3.5 TypeScript production build compatibility.'
