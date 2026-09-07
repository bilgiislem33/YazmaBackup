# R27.3.5 — TypeScript Production Build Fix

Windows'taki gerçek `npm ci` + `tsc --noEmit` ilk kez R26 sonrası frontend'in tamamını derledi ve daha önce statik kaynak kontrollerinin yakalamadığı API drift'lerini ortaya çıkardı.

Düzeltilen kök nedenler:
- eksik Lucide `Building2` ve `Network` importları,
- eski taşınmış ekranların kullandığı `Metric` bileşeni,
- `PageHero` için `description`/opsiyonel icon uyumluluğu,
- `Badge variant` ile yeni `tone` API'si arasındaki geçiş,
- `Progress className`,
- `fmt`, `mins`, `bytes` yardımcıları.

Bunlar UI compile-time uyumluluk düzeltmeleridir. Control Plane API ve Agent runtime değiştirilmedi.
