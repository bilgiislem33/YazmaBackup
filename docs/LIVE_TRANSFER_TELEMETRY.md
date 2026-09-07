# Live Transfer Telemetry — v1.1.0

## Veri yolu

`FileSystemBackupRepository` yeni chunk verisini repository akışına başarıyla yazdıktan sonra `ITransferObserver.OnBytesWritten(...)` çağrısı yapar. Agent'taki `TransferActivityTracker` yalnız bu başarılı write çağrılarını sayar; throughput limiter'dan yalnızca izin alan fakat yazma sırasında hata veren bloklar aktarılmış kabul edilmez.

Agent aktif backup sırasında yaklaşık iki saniyede bir authenticated `POST /api/v1/agent/transfer-telemetry` çağrısı yapar.

Control Plane `TransferTelemetryRegistry` içinde iki farklı veri yapısı tutar:

- Aktif komutların son transfer durumu.
- Son 30 dakikanın 2 saniyelik aggregate hız bucket'ları.

Aggregate history maksimum 1000 bucket ile sınırlıdır. Böylece yüzlerce/binlerce Agent telemetri gönderse bile grafik geçmişi Agent×sample sayısı kadar büyümez. Bu veri ana JSON state dosyasına yazılmaz; backup control-state churn oluşturmaz ve Control Plane yeniden başladığında kısa süreli grafik geçmişinin sıfırlanması veri kaybı sayılmaz.

Admin UI `GET /api/v1/admin/transfer-telemetry` ile anlık toplam hız, aktif transferler ve aggregate history serisini alır. Dashboard yaklaşık iki saniyede bir yenilenir.

## Grafikte gösterilenler

- Anlık toplam aktarım hızı.
- Son 30 dakikalık canlı hız eğrisi.
- Görünen zaman aralığının ortalama hızı.
- Tepe hız.
- Aktif Agent / repository aktarım listesi.
- Aktif komut başına repository'ye yazılmış toplam byte.

## Güvenlik ve dayanıklılık

- Agent token doğrulaması zorunlu.
- `CommandId` zorunlu.
- Operation yalnız `backup`; state yalnız `active`, `completed`, `failed` olabilir.
- Repository identifier doğrulanır.
- Negatif byte/rate reddedilir.
- 8 GiB/s üstü tek-Agent sample reddedilir.
- Sample timestamp çok eski/gelecek olamaz; start timestamp sample'dan sonra olamaz.
- Telemetri hatası yedek verisini veya backup commit sonucunu değiştirmez.
- Telemetri HTTP çağrısı 5 saniyelik fail-safe timeout kullanır.
- History RAM içindedir ve bounded'dır; secret veya repository encryption key içermez.

## SelfTest

SelfTest iki ayrı katmanı doğrular:

1. `TransferTelemetryRegistry` aktif transfer ve bounded aggregate history üretir.
2. Gerçek `FileSystemBackupRepository.PutChunkAsync` çağrısında observer tarafından sayılan byte, başarıyla yazılan chunk boyutuyla birebir eşleşir.
