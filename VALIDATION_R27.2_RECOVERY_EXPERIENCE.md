# R27.2 — Recovery Experience + DR War Room

## Recovery Evidence Timeline
Time Machine benzeri görsel zaman çizgisi artık gerçek `/recovery-runs` ve `/recovery-plans` kanıtlarından oluşur. Ürün restore point API'si olmadığı için sahte restore point oluşturmaz. Seçilen run için doğrulanan veri, en uzun restore süresi ve target başarı oranı gösterilir.

## Disaster Recovery War Room
Mevcut R25 güvenli DR session API'leri gerçek UI aksiyonlarına bağlandı:
- War Room başlat
- current gate'i onayla
- doğrulama notu/kanıtı gir
- doğrula ve sonraki dependency gate'i aç
- oturumu iptal et

Recovery DAG yatay canlı rail olarak görünür. Pending / approved / verified ve current gate görsel olarak ayrılır.

Bu sürüm destructive restore'u sessizce çalıştırmaz; R25'in güvenli execution/gate sınırını korur.

## Korunanlar
R27.0 Command Center/NOC, R27.1 Fleet Galaxy/Device360, R26 single-source React ve R25 PostgreSQL completion fence korunmuştur.

## Görsel yol haritası
R27.3 son görsel zirve sürümüdür: birleşik motion/design system, Executive Mode, navigation polish, accessibility, responsive/density ve bütün modüllerde premium enterprise tutarlılığı.
