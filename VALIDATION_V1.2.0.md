# YazmaBackup v1.2.0 R3 Validation Status


## R2 Windows analyzer düzeltmesi

İlk gerçek Windows `VERIFY.ps1` çalıştırması [1/23]–[13/23] kapılarını geçti; [14/23] Release build sırasında yalnızca 3 warnings-as-errors analyzer bulgusu yakalandı:

- `CA1848` — `MeshCentralConnectorService`: connection test logu `LoggerMessage.Define` ile düzeltildi.
- `CA1848` — `MeshCentralSyncHostedService`: scheduled sync logu `LoggerMessage.Define` ile düzeltildi.
- `CA1859` — `ParseDevices`: dönüş tipi gerçek somut tip olan `MeshCentralInventoryDeviceRecord[]` olarak daraltıldı.

Analyzer suppress/`NoWarn` kullanılmadı ve warnings-as-errors kapısı gevşetilmedi. R2'nin yeniden gerçek Windows VERIFY zincirinden geçirilmesi gerekiyor.

## Bu paket içinde doğrulananlar

- State schema 10 → 11 kod geçişi ve future schema >11 downgrade block.
- MeshCentral connector/inventory/deployment modelleri ve kalıcı state store.
- PostgreSQL migration `008_meshcentral_fleet_connector.sql` transaction sınırları.
- MeshCentral Fleet Connector API/UI çapraz bağımlılıkları.
- Credential'ın ASP.NET Data Protection ile korunması; API DTO'sunda secret geri dönüşü olmaması.
- `ybboot_` ve `ybpkg_` tek kullanımlık ticket yapısı.
- Agent package SHA-256 zorunluluğu.
- JavaScript syntax (`node --check`) PASS.

## Bu çalışma ortamında çalıştırılamayan kapılar

Bu ChatGPT çalışma konteynerinde .NET SDK bulunmadığı için `dotnet restore`, Release build ve C# self-test burada çalıştırılamadı. `scripts/VERIFY.ps1` Windows/.NET ortamında bu kapıları zorunlu olarak çalıştırmaya devam eder.

## Henüz gerçek ortamda doğrulanmayanlar

- `https://mesh.gursoyoto.com.tr` gerçek servis hesabıyla bağlantı.
- Gerçek MeshCentral cihaz listesinin alınması.
- Gerçek Windows PC'ye MeshCentral RunCommand ile YazmaBackup Agent kurulumu.
- Agent heartbeat/version kanıtı.
- Gerçek NAS restore/fault matrix.
- Normalized PostgreSQL runtime cutover.

Bu maddeler tamamlanana kadar `productionReady=false` korunur.
## R3 gerçek MeshCentral saha doğrulaması

02.09.2026 saha testinde `mesh.gursoyoto.com.tr` için WSS erişimi ve authentication başarılı oldu. `ServerInfo` native text çıktı döndürdü; `ListDevices --json` gerçek Node ID, `name`, `rname`, `conn`, `pwr`, `groupname` ve OS alanlarını içeren fleet JSON'u üretti. R3 connector bu gerçek sözleşmeye uyarlanmıştır.

Henüz PASS verilmeyen kapılar: YazmaBackup UI üzerinden R3 connector sync, tek test PC'ye RunCommand ile Agent kurulumu ve heartbeat/eşleşme kanıtı.
