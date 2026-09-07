$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$ui=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\components\console.tsx')
$pkg=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\YazmaBackup.Frontend\package.json')
$verify=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'scripts\VERIFY.ps1')
if($pkg.Contains('@tremor/react')){throw 'React 19 ile uyumsuz @tremor/react bağımlılığı kalmış.'}
if($ui.Contains('from "@tremor/react"')){throw 'Tremor kaynak importu kalmış.'}
if(-not $verify.Contains("'Npgsql' = '10.0.0'")){throw 'Npgsql explicit allowlist gate eksik.'}
if($verify.Contains("if (Get-ChildItem .\src -Recurse -File -Filter *.csproj | Select-String -Pattern '<PackageReference'")){throw 'Eski tüm PackageReference reddeden gate kalmış.'}
Write-Host 'PASS: R27.3.1 dependency + VERIFY root-cause fix.'
