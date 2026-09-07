# Modern Light UX — v1.1.0

v1.1.0 Control Plane arayüzü tamamen light tema ve sade kullanıcı akışı ile yenilenmiştir. Harici CDN, JS framework veya font bağımlılığı yoktur.

## Tasarım ilkeleri

- Segoe UI / sistem fontları
- Beyaz yüzey + açık gri arka plan
- Büyük, okunabilir metrik kartları
- Teknik terimlerin yanında açıklama
- Kritik/uyarı/sağlıklı durumlarının hem metin hem renk ile gösterimi
- Tek tık hızlı işlemler
- 14 top-level modül (Kurulum & Pilot Merkezi dahil)
- Responsive sidebar
- Ctrl+K global arama
- CSP ile uyumlu harici `app.js` + `styles.css`; inline script/style yok
- DOM'a sunucu verisi yazarken `textContent/createElement`; `innerHTML/eval/new Function` yok

## Canlı grafik

`/api/v1/admin/transfer-telemetry` iki saniyede bir sorgulanır. Canvas çizimi harici chart kütüphanesi kullanmaz. Grafik son 30 dakikalık bounded transfer history'den üretilir.

## Kolay restore

Restore ekranı kullanıcıdan Backup ID istemez. Seçilen yedek politikası üzerinden Agent/source/repository bilgileri otomatik belirlenir; Agent restore point listesini döndürür.
