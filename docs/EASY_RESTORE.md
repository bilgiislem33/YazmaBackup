# Easy Restore Wizard — v1.1.0

Kullanıcı artık manifest/Backup ID bilmek zorunda değildir.

1. Aktif yedek politikasını seçer.
2. UI, policy'den Agent/source/repository bilgisini doldurur.
3. Control Plane `ListRestorePoints` komutunu Agent'a gönderir.
4. Agent repository manifestlerini doğrulayarak son 500 restore point'i listeler.
5. Kullanıcı tarih/saat seçer.
6. Güvenli hedef klasörü belirler.
7. Mevcut restore engine SHA-256 bütünlük doğrulamasıyla dosyaları geri yükler.

Varsayılan UX, canlı kullanıcı klasörünün üzerine doğrudan yazmak yerine ayrı hedef klasör kullanılmasını teşvik eder.
