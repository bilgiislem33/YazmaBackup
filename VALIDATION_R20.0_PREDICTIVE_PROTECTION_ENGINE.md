# R20.0 — Predictive Protection Engine

## Büyük değişiklik
R19 mevcut sorunları çözüm planına çeviriyordu. R20 bazı sorunları oluşmadan önce görünür hale getirir.

### Dört erken uyarı
1. Backup SLA riski: politika henüz gecikmemiş olsa bile Agent çevrimdışıysa ve son backup geçmişi sorunluysa erken uyarı üretir.
2. NAS kapasite tarihi: Repository Health tarafından ölçülmüş günlük büyüme ve EstimatedDaysToFull kullanılır; uydurma trend oluşturulmaz.
3. Restore kanıtı eskimesi: restore-drill aralığının %80'ine gelindiğinde kanıt eskimeden uyarı verir. Hiç restore kanıtı yoksa bunu açıkça belirtir.
4. Agent kararlılığı: çevrimdışı Agent ile son 24 saatte tekrarlanan tamamlanmış komut hataları birlikteyse erken risk üretir.

## Güven yüzdesi
Tahminler kesinlik iddiası değildir. Her tahmin bir ConfidencePct taşır ve UI bunu gösterir.

## Güvenlik
R20 read-only tahmin motorudur. Endpoint, NAS veya retention üzerinde sessiz değişiklik yapmaz. Riskli işlemler R19/R10.4 onay zincirinde kalır.

## Agent
Agent runtime/protokolü değişmedi.
