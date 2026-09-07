$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$config = Join-Path $env:ProgramData 'YazmaBackup\agent-config.json'
if (-not (Test-Path -LiteralPath $config)) { throw "Agent yapılandırması bulunamadı: $config. Önce BOOTSTRAP_AGENT.ps1 çalıştırın." }
dotnet run --project "$root\src\YazmaBackup.Agent\YazmaBackup.Agent.csproj" -c Release --
