# YazmaBackup v1.1.0 — Güvenli Self-Healing

Self-Healing, sınırsız PowerShell veya rastgele otomatik değişiklik motoru değildir. v1.1.0 yalnız düşük riskli ve kod içinde allowlist edilmiş eylemleri otomatik uygular.

## Otomatik uygulanabilen eylemler

### `reset-repository-circuit`

Yalnız repository circuit-breaker hata/backoff state'ini temizler. NAS üzerindeki yedek verisine, manifestlere veya anahtarlara dokunmaz. NAS erişiminin gerçekten düzeldiği doğrulandıktan sonra kullanılmalıdır.

### `cleanup-stale-restore-temp`

Yalnız seçilen kök altında `.yazmabackup-restore-*.tmp` desenine uyan ve 24 saatten eski geçici dosyaları temizler. Başka dosya desenleri silinmez.

## Otomatik uygulanmayan sorunlar

- VSS servisi veya writer hataları,
- NTFS/USN konfigürasyonu,
- NAS ACL/SMB yetkileri,
- repository encryption key değişiklikleri,
- ransomware protection lock,
- Windows servis hesabı değişikliği.

Bu alanlar teşhis edilir ve çözüm önerisi verilir; kullanıcı onayı olmadan sistem konfigürasyonu değiştirilmez.
