# Otomatik Restore Drill

YazmaBackup v1.1.0, yedeğin yalnız yazılmasını değil geri yüklenebilirliğini de periyodik olarak doğrular.

## Akış

1. Policy scheduler `RestoreDrillIntervalDays` süresi geldiğinde `RestoreDrill` komutu üretir.
2. Agent repository için yerel DPAPI korumalı key ring'i açar.
3. İlgili kaynak yolunun en yeni restore point'i seçilir.
4. Restore, `%ProgramData%\YazmaBackup\restore-drills\<CommandId>` altında izole geçici alana yapılır.
5. `RestoreEngine` her chunk SHA-256 değerini, dosya uzunluğunu ve tam dosya SHA-256 değerini doğrular.
6. Test başarılıysa geçici alan silinir ve doğrulanan dosya/byte sayısı Control Plane'e sonuç olarak döner.
7. Cleanup başarısızsa drill başarılı sayılmaz; operatör müdahalesi gerekir.

## Zamanlama

Varsayılan restore drill periyodu 7 gündür. 1–365 gün arasında policy bazında değiştirilebilir. Saatlik yedek almak restore drill'in de saatlik yapılacağı anlamına gelmez; iki takvim birbirinden bağımsızdır.

## Güvenlik

Restore drill üretim kaynak klasörüne yazmaz ve `overwriteExisting=false` kullanır. Geçici hedef Agent ProgramData alanındadır. Restore sırasında repository bütünlük hatası, eksik key, bozuk AES-GCM etiketi, chunk SHA-256 uyuşmazlığı veya tam dosya SHA-256 uyuşmazlığı drill'i başarısız yapar.

## Manuel çalıştırma

```powershell
.\scripts\RESTORE_DRILL.ps1 `
  -Server "https://backup.gursoyoto.com.tr" `
  -AccessToken $env:YAZMABACKUP_MANAGEMENT_TOKEN `
  -AgentId "AGENT-GUID" `
  -RepositoryRoot "\\NAS01\YazmaBackup" `
  -RepositoryId "NAS01-MERKEZ" `
  -SourceRoot "C:\Muhasebe"
```
