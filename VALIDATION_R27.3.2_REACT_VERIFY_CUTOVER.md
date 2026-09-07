# R27.3.2 — React VERIFY Cutover Fix

R26'da source `wwwroot` bilinçli olarak kaldırılmış olmasına rağmen ana VERIFY'ın eski R5/R6 UI kapıları hâlâ `ControlPlane/wwwroot/index.html`, `app.js` ve `styles.css` okumaya çalışıyordu. Bu yüzden R27.3.1 dependency düzeltmesinden sonra Windows doğrulaması [9/23]'te yanlış negatif veriyordu.

Bu sürüm eski UI dosyalarını geri getirmez. Ana kalite kapısı güncel mimariye taşınmıştır:
- React/Next tek kaynak kontrolü,
- Türkçe layout,
- ana React modülleri,
- `dangerouslySetInnerHTML` / eval / Function güvenlik kapısı,
- backend UX API ve CSP invariantları,
- zero-touch lifecycle runtime invariantları + React LifecyclePage,
- legacy `wwwroot` ve `legacy-ui` yokluğu.

Eski R6 HTML/CSS operation-card/guided-wizard kapıları kaldırılmıştır; bunlar artık üretimde olmayan UI kaynağını doğruluyordu. R26/R27 özel React kapıları korunmuştur.

Runtime, deployment binding ve Agent değiştirilmemiştir.
