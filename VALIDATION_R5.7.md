# YazmaBackup v1.2.0 R5.7 — Windows Service Configuration Determinism

## Kök neden
R5.6 sahada ACL, native exit-code ve bootstrap aşamalarını geçti; mevcut Windows servisinin `sc.exe config` ile güncellenmesi `ExitCode=1639` döndürdü. Kök neden, `ImagePath` içindeki tırnaklı `Program Files` yolu ile `sc.exe` anahtar/değer sözdiziminin Windows PowerShell 5.1 native argüman aktarımına bağımlı olmasıdır.

## R5.7 kalıcı düzeltme
- Mevcut servis güncellemesi `Win32_Service.Change` ile named/typed WMI parametreleri üzerinden yapılır.
- LocalSystem yeni servis kurulumu `New-Service` ile kalır.
- gMSA servis oluşturma `Win32_Service.Create` kullanır; `sc.exe create` kaldırıldı.
- Rollback `Win32_Service.Change`, yeni servis temizliği `Win32_Service.Delete` kullanır; `sc.exe config/delete` kaldırıldı.
- Description registry üzerinden yazılır.
- Recovery/failureflag komutları yalnız sabit, quote gerektirmeyen parametrelerle R5.6 `ProcessStartInfo` yakalayıcısı üzerinden çalışır.
- Servis `ImagePath` registry doğrulaması ve 5 saniyelik Running kararlılık penceresi korunur.

## Statik kabul kapıları
- `Invoke-ServiceWmiChange`, `GetMethodParameters('Change')`, `New-GmsaServiceWmi`, `GetMethodParameters('Create')` bulunmalı.
- Installer install/update/rollback yolunda `sc.exe config`, `sc.exe create`, `sc.exe delete` bulunmamalı.
- R5.6 ACL recovery ve bootstrap-safe logging kapıları korunmalı.

## Saha kabulü
Hedefte eski `YazmaBackupAgent` servisi mevcutken yeniden Agent Kur çalıştırılmalı. Beklenen sıra: ACL PASS -> bootstrap stored securely -> R5.7 service configuration PASS -> service Running -> enrollment/heartbeat success.
