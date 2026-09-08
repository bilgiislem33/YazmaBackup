# Retention Safety Model

R29.1 retention işlemini doğrudan manifest silme modelinden, geri alınabilir karantina modeline taşır.

## Güvenlik kuralları

- `IBackupRepository` artık manifest veya chunk silme işlemi sunmaz; fiziksel temizleme yalnız retention-safe sözleşmesindedir.
- Backup ve retention aynı repository mutation lease'ini kullanır. Bu lease dosya tabanlı ve process'ler arasıdır.
- Retention önce plan çıkarır, sonra global envanteri okur ve plan envanterinin değişmediğini doğrular.
- İmmutability süresi dolmamış restore point repository katmanında karantinaya alınamaz.
- Aktif manifest yalnız manifest, bütünlük sidecar'ı ve karantina metadata'sı kalıcı staging dizinine yazılıp atomik olarak yayınlandıktan sonra kaldırılır.
- Karantina süresi yedi gündür. Repository daha erken fiziksel purge isteğini reddeder.
- Karantinadaki manifestler purge edilene kadar chunk garbage collection ve encryption-key kullanım hesabına dahil edilir.
- Yarım kalmış `.staging-*` dizinleri karantina envanterine alınmaz; aktif manifest atomik yayın tamamlanmadan kaldırılmadığı için crash halinde restore point korunur.
- Manifest karantinası veya purge başarısız olursa retention durur ve chunk temizliğine geçmez.

## Operasyonel sınır

Bu korumalar kaynak kodu ve otomatik Windows CI testleriyle doğrulanır. `productionReady` yine `false` kalır; gerçek SMB/NAS bağlantı kopması, disk dolması, güç kesintisi ve canlı restore tatbikatı pilot ortamında ayrıca tamamlanmalıdır.
