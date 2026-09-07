# R26.0 — Real React/Next.js Production Cutover

## Düzeltme
R9/R10 belgelerindeki production cutover iddiası mevcut kaynak yapısıyla kanıtlanmıyordu. R26 bunu düzeltir.

## Tek UI kaynağı
`src/YazmaBackup.Frontend` tek UI source-of-truth'tur.
- `src/YazmaBackup.ControlPlane/wwwroot` kaynak olarak kaldırıldı.
- `src/YazmaBackup.ControlPlane/legacy-ui` kaldırıldı.
- eski legacy rollback script kaldırıldı.

`wwwroot` artık yalnız `dotnet publish` çıktısında oluşan artifact'tır.

## Zorunlu publish zinciri
`YazmaBackup.ControlPlane.csproj` içindeki `BuildProductionReactFrontend` target'ı `ComputeFilesToPublish` öncesinde:
1. BUILD_FRONTEND.ps1
2. npm ci
3. TypeScript typecheck
4. Next.js build/static export
5. export dosyalarını publish/wwwroot içine `ResolvedFileToPublish`
zincirine bağlar.

Frontend build başarısızsa Control Plane publish başarısızdır.

## Deterministik dependency kapısı
Production build `package-lock.json` olmadan FAIL olur ve yalnız `npm ci` kullanır.
R25 kaynağında package-lock yoktu. Paketleme ortamında registry erişimi olmadığı için sahte lock üretilmedi.
Windows'ta README_BUILD.md bootstrap komutu bir kez çalıştırılmalı ve oluşan package-lock.json kaynak pakete/repository'ye eklenmelidir.
Bu yapılmadan VERIFY React cutover için PASS vermez.

## VERIFY düzeltmesi
R6.5-R10 arasındaki eski wwwroot/legacy-ui ve React source-string "cutover" kapıları kaldırıldı.
Yeni kapı:
- legacy-ui yok,
- source wwwroot yok,
- MSBuild publish target gerçek,
- npm ci zorunlu,
- gerçek Next.js build/export oluşuyor,
- `_next` artifact'ı var
şartlarını arar.

## .slnx
Next.js bir .NET project değildir; sahte `.csproj` wrapper eklenmedi. Production bağı doğrudan ControlPlane MSBuild publish target'ıdır. Bu, `dotnet publish` ile UI build'inin kaçamamasını sağlar.

## Agent
Agent değişmedi.
