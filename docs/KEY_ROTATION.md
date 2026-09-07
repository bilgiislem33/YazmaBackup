# Repository Key Rotation Runbook

1. Yeni 256-bit key üretin ve benzersiz KeyId verin.
2. Repository'yi kullanan tüm Agent'lara aynı yeni key'i `MakeActive=true` ile provision edin.
3. Yeni backup'ların yeni key ID'siyle yazıldığını kontrol edin.
4. Her Agent/repository için `SCRUB_REPOSITORY.ps1 -MigrateLegacyPlaintext $true` çalıştırın.
5. Scrub sonucu `VerifiedChunks`, `VerifiedManifests` ve `MigratedChunks` değerlerini kaydedin.
6. Restore smoke test çalıştırın.
7. `REMOVE_REPOSITORY_KEY_REMOTE.ps1` ile eski key'i retire edin.
8. Agent repository header taramasında eski key referansı bulursa removal reddedilir; scrub/rekey tekrar çalıştırılmalıdır.
9. Tüm Agent'lar eski key'i bıraktıktan sonra operasyon kaydını kapatın.

Aktif key doğrudan silinemez. Eski key repository referansı varken de silinemez.
