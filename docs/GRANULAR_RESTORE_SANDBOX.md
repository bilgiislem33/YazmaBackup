# YazmaBackup v1.1.0 — Granular Restore & Restore Sandbox

## Granular Restore

Bir restore point içinden tüm yedeği açmak yerine yalnız seçilen göreli dosya/klasör yolları geri yüklenebilir.

Örnek:

```text
Belgeler/Teklif.xlsx
Muhasebe/2026
```

Path traversal engellenir; `..` selector kabul edilmez ve hedef dosyaların tamamı restore kökü altında kalmak zorundadır. Chunk SHA-256 ve tam dosya SHA-256 doğrulaması normal restore ile aynıdır.

## Restore Sandbox

Restore point canlı kullanıcı klasörüne dokunmadan ayrı bir çalışma alanına açılır. Varsayılan UI limiti 5.000 dosya ve 20 GiB'dır. Restore motoru:

- her chunk hash'ini,
- dosya uzunluğunu,
- tam dosya SHA-256 değerini

doğrular.

Sandbox başarılı olduğunda sonuç `verified` olarak döner. Sandbox otomatik production cutover yapmaz; doğrulanmış dosyaların canlı konuma taşınması ayrıca kontrollü restore işlemidir.
