# YazmaBackup Mimari Kararları — v1.1.0

## Katmanlar

1. **Domain**: Agent, command, manifest, retention ve backup policy modelleri.
2. **Contracts**: Agent/Control Plane API DTO'ları; Domain'e açık ProjectReference ile bağlıdır.
3. **Application**: Backup/restore/retention use-case'leri; repository, snapshot, chunker ve change tracker arayüzleri.
4. **Infrastructure**: Gear CDC, AES-GCM repository, VSS, USN Journal, bandwidth limiter ve Windows session probe.
5. **Agent**: Windows Service, DPAPI secret store, RSA key exchange, command execution journal, update staging.
6. **Control Plane**: enrollment, Agent auth, command lease, scheduler, policy API, management authentication/RBAC, audit ve Türkçe web UI.
7. **SelfTest**: veri yolu ve Control Plane davranış testleri.
8. **SigningTool**: update imza anahtarı/metadata yardımcı aracı.

## Veri yolu

Backup sırasında Control Plane yalnız komut/policy yönetir. Veri Agent'tan NAS'a doğrudan gider. MeshCentral backup data proxy değildir.

```text
Control Plane ──HTTPS──> Agent ──SMB/NAS──> Repository
       ▲                   │
       └── heartbeat/result┘
```

## Artımlı yedek güvenlik modeli

USN Journal optimizasyondur, doğruluğun tek kaynağı değildir.

- Önce previous manifest okunur.
- USN change boundary snapshot'tan önce alınır.
- Journal ID değişmiş, wrap olmuş, checkpoint geçersiz veya record çözülememişse `CanReuse=false` ve tam tarama yapılır.
- Snapshot oluşturulur.
- Yalnız USN açısından değişmemiş **ve** length/mtime önceki manifestle aynı dosyalar manifest referansı olarak reuse edilir.
- Manifest atomik yazılır.
- USN checkpoint bundan sonra commit edilir.

Bu sıra, snapshot sonrası oluşan USN kayıtlarının checkpoint tarafından yutulmasını engeller.

## Repository formatı

- Chunk adresi: plaintext SHA-256.
- Chunk içeriği: AES-256-GCM envelope.
- Manifest: AES-256-GCM envelope + stored-byte SHA-256 sidecar.
- Metadata: AES-256-GCM authenticated `repository.meta`.
- AAD domain separation: `chunk|...`, `manifest|...`, `repository-metadata|...`.
- Key ring: active key + restore/rekey için eski keyler.

Dedup plaintext içeriğin hashine dayandığı için aynı repository anahtar setini kullanan Agent'lar ortak chunk'ı paylaşabilir.

## Key rotation

Repository açıldığında authenticated metadata eski active key'i gösteriyor, Agent key ring'de yeni active key varsa metadata `rekeying` durumuna taşınır. Touched chunk'lar active key'e yeniden yazılır; tam scrub tüm chunk/manifestleri active key'e geçirir. Scrub sonunda metadata `complete` olur.

Eski key silme ayrı bir güvenlik kapısıdır: repository metadata/chunk/manifest encrypted headerları taranır; key hâlâ referanslıysa removal reddedilir.

## Legacy migration

Encrypted repository normal backup/restore sırasında plaintext objeye izin vermez. Plaintext yalnız constructor'a açıkça `allowLegacyInitialization=true` veren scrub/migration yolu içinde ve metadata `complete` olmadan okunabilir. Bu sayede authenticated eski `mixed` metadata replay edilse bile normal operasyon downgrade penceresi açmaz.

## Retention / GC

GFS seçimi source+Agent restore point'leri üzerinde yapılır. Manifestler silindikten sonra **tüm repository manifestleri** tekrar mark edilir. Referanssız chunk yalnız 24 saat grace period sonrasında silinir; bu, eşzamanlı/in-flight backup ile GC yarışını azaltır.

Production-grade multi-node runtime için repository-wide distributed lease/transaction koordinasyonu normalized PostgreSQL store etkinleştiğinde uygulanacaktır.

## Control Plane state

v1.1.0 tek-node atomik JSON adapter kullanır:

- temp + WriteThrough + fsync + atomic move,
- önceki primary `.bak`,
- bozuk primary için `.bak` recovery,
- command başına süreli lease,
- max retry,
- Agent başına pending queue limiti,
- 30 günlük completed-command pruning,
- policy `NextRunAtUtc` ve command enqueue aynı state commit'inde.

Bu storage katmanı `IControlPlaneStore` arkasındadır. PostgreSQL şema/migration sözleşmesi v1.1.0 paketinde korunur; aktif runtime store halen tek-node atomik JSON adapterdır. Doğrulanmış güncel provider ile normalized PostgreSQL runtime adapter üretim/HA kapısında eklenecek ve Agent/data-plane sözleşmesi korunacaktır.
