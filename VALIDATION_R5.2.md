# YazmaBackup v1.2.0 R5.2 — Multi Folder Selection UX

## Kapsam
- Uzaktan klasör tarayıcısında her klasör için checkbox.
- Aynı görünümde tümünü seç / seçimi temizle.
- Klasör içine girildiğinde önceki seçimlerin korunması.
- Alt klasör seçimi için üst satırda kısmi seçim (indeterminate) görünümü.
- Politika oluştururken çoklu seçim, mevcut tek-source backend modeli korunarak her seçili klasör için ayrı politika oluşturur.
- Anlık yedeklemede çoklu seçim, her klasör için ayrı idempotent komut kuyruğa alır.
- Pilot ekranı geriye uyumlu tek kaynak davranışını korur.
- Sistem klasörleri işaretlenirken kullanıcı uyarılır.

## Kabul kriteri
1. C:\ altında iki veya daha fazla klasör aynı anda işaretlenebilir.
2. Alt klasöre girip seçim yapıldıktan sonra üst klasöre dönüldüğünde seçim kaybolmaz.
3. `N Klasörü Ekle` seçilen klasör sayısını gösterir.
4. Çoklu politika kaydında her kaynak ayrı SourcePath ile oluşturulur.
5. Çoklu anlık yedeklemede her kaynak ayrı komut olarak kuyruğa alınır.
