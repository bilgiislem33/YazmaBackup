# Alarm Merkezi — v1.1.0

`OperationalSentinelService` her 30 saniyede liderlik lease'i almayı dener. Yalnız sentinel lideri alarm değerlendirmesi yapar.

Otomatik alarm kaynakları:

- Agent ransomware/protection lock → **critical**
- Agent 5 dakikadan uzun heartbeat vermedi → **warning**
- Etkin yedek politikası tolerans penceresini geçti → **warning**
- Son backup komutu başarısız → **warning**
- Son restore-drill başarısız → **critical**

Alarmlar fingerprint ile upsert edilir. Aynı olay yeni satırlar üretmez; `LastSeenAtUtc` ilerler. Operatör alarmı onaylayabilir. Koşul normale döndüğünde sentinel alarmı `resolved` durumuna taşır; geçmiş kayıt denetim amacıyla saklanır.
