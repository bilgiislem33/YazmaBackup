# R25.0 — DR Execution Controller + Consistency Fence

## Büyük sıçrama
R24 Recovery DAG hesaplıyordu. R25 bu DAG'ı kalıcı bir DR execution session'a dönüştürür:
`pending -> approved -> verified -> next step`.
Bir adım doğrulanmadan sonraki dependency-safe adıma geçilemez.

## Kritik Active-Active doğruluk düzeltmesi
R17'den kalan completion lease yarış penceresi kapatıldı.
PostgreSQL command satırı `FOR UPDATE` altında:
- exact `lease_id` doğrulanır,
- lease expiry doğrulanır,
- completion execution identity `completion_lease_id` ile saklanır,
- farklı/stale lease completion reddedilir.

Bu kontrol artık yalnız process içindeki StateStore doğrulamasına güvenmez; DB transaction sınırında da yapılır.

## Idempotency index
Eski `(cluster_id, agent_id, idempotency_key)` unique index yerine command type dahil:
`(cluster_id, agent_id, command_type, idempotency_key)`.
Aynı idempotency key farklı command type için yanlış dedupe oluşturmaz.

## DR session güvenlik modeli
- Recovery DAG cycle varsa session başlamaz.
- Yalnız `CurrentOrder` adımı onaylanabilir.
- Onaylanmamış adım verified yapılamaz.
- Verified olmadan sonraki adım açılmaz.
- Session state StateStore CAS ile kalıcıdır.
- R25 destructive restore'u sessizce tetiklemez; execution safety/gate katmanıdır.

## Agent
Agent runtime/protokolü değişmedi.
