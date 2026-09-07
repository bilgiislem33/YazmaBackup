$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host 'YazmaBackup modern .NET template baseline kontrolü'

$globalJson = Get-Content -Raw -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json
if ($globalJson.sdk.version -notmatch '^10\.') {
    throw "Beklenen .NET 10 SDK baseline bulunamadı. global.json=$($globalJson.sdk.version)"
}
if ($globalJson.sdk.allowPrerelease -ne $false) {
    throw 'Prerelease SDK kullanımı kapalı olmalıdır.'
}
if ($globalJson.sdk.rollForward -ne 'latestFeature') {
    throw 'global.json rollForward latestFeature olmalıdır.'
}

$props = Get-Content -Raw -LiteralPath (Join-Path $root 'Directory.Build.props')
foreach($expected in @(
    '<TargetFramework>net10.0</TargetFramework>',
    '<LangVersion>14.0</LangVersion>',
    '<Nullable>enable</Nullable>',
    '<ImplicitUsings>enable</ImplicitUsings>',
    '<AnalysisLevel>latest-recommended</AnalysisLevel>',
    '<EnableNETAnalyzers>true</EnableNETAnalyzers>'
)) {
    if ($props -notmatch [Regex]::Escape($expected)) {
        throw "Template baseline eksik: $expected"
    }
}

$slnx = Join-Path $root 'YazmaBackup.slnx'
if (-not (Test-Path -LiteralPath $slnx -PathType Leaf)) {
    throw 'Modern .slnx solution dosyası bulunamadı.'
}

Write-Host "PASS: .NET SDK baseline = $($globalJson.sdk.version)"
Write-Host 'PASS: Target Framework = net10.0'
Write-Host 'PASS: C# = 14.0'
Write-Host 'PASS: Nullable + ImplicitUsings + latest-recommended analyzers'
Write-Host 'PASS: Modern .slnx solution formatı'
