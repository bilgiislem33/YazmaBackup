# R17.0 — Multi-Aggregate Atomic Completion

## Basit anlatım
Bir yedekleme veya geri yükleme komutu bittiğinde sistem yalnızca `Komut tamamlandı` yazmaz. Sonuç; repository sağlık kayıtlarını, recovery drill durumunu, alarm/dayanıklılık verilerini ve operasyon geçmişini de etkileyebilir.

Önceki güvenli mimaride bu yan etkiler korunuyordu fakat completion hâlâ genel compatibility transaction yolundan geçiyordu.

R17 bu kritik bitiş noktasını PostgreSQL transaction motoruna taşır.

## Yeni davranış
Komut sonucu geldiğinde:
1. exact command satırı PostgreSQL'de kilitlenir,
2. Control Plane state version doğrulanır,
3. command tamamlanır,
4. mevcut resilience kuralları uygulanır,
5. ortaya çıkan son state normalize PostgreSQL tablolara aynı transaction içinde yansıtılır,
6. hepsi birlikte COMMIT edilir.

Herhangi bir aşama başarısızsa transaction tamamı rollback olur.

Bunun pratik anlamı: sistem `backup başarılı` deyip recovery/repository tarafını eski durumda bırakamaz.

## Korunan güvenlikler
- PostgreSQL Serializable transaction
- exact command row lock
- state version CAS
- R16 `FOR UPDATE SKIP LOCKED` claim
- lease ownership
- R15 focused enqueue/policy mutations
- R14 direct SQL reads
- compatibility snapshot / DR geri dönüş yolu

## Agent
Agent protokolü ve runtime değişmedi.
