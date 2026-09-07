# Legacy Repository Migration Runbook

Amaç, eski plaintext v1/v2 repository verisini veri kaybetmeden schema 3 + AES-256-GCM formatına yükseltmektir.

1. NAS repository'nin ayrı snapshot/kopyasını alın.
2. İlgili repository key'ini Agent'a provision edin.
3. Normal backup/restore başlatmayın; encrypted Agent legacy repository'yi otomatik kabul etmeyecektir.
4. `SCRUB_REPOSITORY.ps1 -MigrateLegacyPlaintext $true` çalıştırın.
5. Scrub her chunk plaintext SHA-256'ını doğrular.
6. Legacy manifest full-file SHA-256'ı chunk'lardan yeniden kurularak schema 3'e yükseltilir.
7. Chunk ve manifestler AES-256-GCM ile yeniden yazılır.
8. `repository.meta` authenticated olarak `complete` durumuna alınır.
9. Point-in-time/normal restore smoke test yapın.
10. Migration sonrasında görülen plaintext object bütünlük ihlali olarak reddedilir.

Migration yarıda kalırsa metadata `mixed/rekeying` kalabilir; aynı explicit scrub güvenle yeniden çalıştırılabilir. Normal backup/restore bu geçiş penceresini kendiliğinden açmaz.
