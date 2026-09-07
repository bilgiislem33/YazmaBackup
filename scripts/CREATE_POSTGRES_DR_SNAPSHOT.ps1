param(
 [Parameter(Mandatory=$true)][string]$EncryptionCertificateThumbprint,
 [string]$OutputDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($env:PGHOST) -or [string]::IsNullOrWhiteSpace($env:PGDATABASE) -or [string]::IsNullOrWhiteSpace($env:PGUSER)){throw 'pg_dump için PGHOST, PGDATABASE ve PGUSER environment değişkenleri gereklidir. Parolayı komut satırına yazmayın; PGPASSFILE/kurumsal secret injection kullanın.'}
$pgDump=Get-Command pg_dump -ErrorAction SilentlyContinue
if(-not $pgDump){throw 'pg_dump PATH içinde bulunamadı. PostgreSQL client tools yüklenmelidir.'}
$thumb=($EncryptionCertificateThumbprint -replace '\s','').ToUpperInvariant()
$cert=Get-Item -LiteralPath ('Cert:\LocalMachine\My\'+$thumb) -ErrorAction Stop
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$temp=Join-Path ([IO.Path]::GetTempPath()) ('YB-PGDR-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force|Out-Null
try{
 $dump=Join-Path $temp 'control-plane.dump'
 & $pgDump.Source --format=custom --no-owner --no-acl --file=$dump
 if($LASTEXITCODE -ne 0){throw ('pg_dump failed with exit code '+$LASTEXITCODE)}
 $hash=(Get-FileHash -LiteralPath $dump -Algorithm SHA256).Hash.ToLowerInvariant()
 $meta=[ordered]@{schemaVersion='1';product='YazmaBackup';engine='postgresql';createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');dumpSha256=$hash;dumpLength=(Get-Item $dump).Length}
 $meta|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $temp 'manifest.json') -Encoding UTF8
 $zip=Join-Path $temp 'transactional-dr.zip'
 Compress-Archive -Path $dump,(Join-Path $temp 'manifest.json') -DestinationPath $zip -CompressionLevel Optimal
 $b64=[Convert]::ToBase64String([IO.File]::ReadAllBytes($zip),[Base64FormattingOptions]::InsertLineBreaks)
 $b64Path=Join-Path $temp 'transactional-dr.b64';$b64|Set-Content -LiteralPath $b64Path -Encoding ASCII
 $out=Join-Path $OutputDirectory ('YazmaBackup_Postgres_DR_'+(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')+'.ybpgdr.p7m')
 Protect-CmsMessage -Path $b64Path -To $cert -OutFile $out
 Write-Host ('PASS: PostgreSQL transactional DR snapshot: '+$out)
 Write-Host ('SHA-256: '+((Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()))
}finally{Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue}
