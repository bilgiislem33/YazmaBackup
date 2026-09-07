# YazmaBackup v1.1.0 — Validation Fabric

v1.1.0, pilot hazırlığını yalnız konfigürasyon kontrolünden gerçek Windows/NAS doğrulamasına taşır.

## Derin doğrulama matrisi

Agent üzerinde aşağıdaki kontroller çalışır:

- kaynak klasör erişimi,
- gerçek Windows VSS snapshot oluşturma, okunabilirlik ve cleanup,
- NTFS USN Journal capture ve güvenli fallback durumu,
- NAS/repository üzerinde 64 KiB `WriteThrough` yazma + silme testi,
- repository boş alanı,
- AES repository key-ring durumu,
- repository circuit-breaker durumu.

Her kontrol `pass / warn / fail`, süre (ms), açıklama ve önerilen aksiyon döndürür. Test sonuçları canlı veri veya NAS içeriğini değiştirmek için kullanılmaz; yalnız geçici validation dosyası oluşturulur ve silinir.

## Fault-matrix yaklaşımı

Gerçek üretim doğrulama puanı, Windows üzerinde aşağıdaki senaryolar geçirilmeden artırılmaz:

1. VSS açık Office/PST dosyası,
2. USN normal capture,
3. USN journal değişimi/wrap → full-hash fallback,
4. SMB/NAS erişim kesintisi,
5. NAS yetki reddi,
6. düşük disk alanı,
7. Agent restart sonrası circuit state,
8. restore sandbox SHA-256 doğrulaması,
9. granular restore,
10. update staging + rollback kanıtı.

Bu pakette framework ve komut sözleşmeleri hazırdır; gerçek Windows/NAS yürütmesi kullanıcı ortamındaki `VERIFY.ps1` ve pilot runbook ile yapılmalıdır.
