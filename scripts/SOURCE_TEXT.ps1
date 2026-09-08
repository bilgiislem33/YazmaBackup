# Source ownership for gates after the Control Plane modular cutover.
function Get-YazmaBackupControlPlaneSource {
    param([Parameter(Mandatory=$true)][ValidateSet('Endpoints','State')][string]$Area)
    $controlPlane = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\YazmaBackup.ControlPlane'
    if ($Area -eq 'Endpoints') {
        $paths = @((Join-Path $controlPlane 'Program.cs'))
        $paths += @(Get-ChildItem (Join-Path $controlPlane 'Hosting'),(Join-Path $controlPlane 'Endpoints') -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
    } else {
        $paths = @(Get-ChildItem $controlPlane -File -Filter 'StateStore*.cs' | Select-Object -ExpandProperty FullName)
    }
    if ($paths.Count -lt 2) { throw "Control Plane $Area source modules are missing." }
    return (($paths | Sort-Object | ForEach-Object { Get-Content -LiteralPath $_ -Raw -Encoding UTF8 }) -join "`n")
}
