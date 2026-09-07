# YazmaBackup v1.2.0 R5 - MeshCentral Installer Reliability

## Saha kök nedenleri
R4 gerçek hedefte ZIP indirme/açma ve `INSTALL_AGENT.ps1` yürütme noktasına ulaştı. İki hata sahada doğrulandı: PowerShell Execution Policy child installer'ı engelleyebiliyordu ve child `powershell.exe` başarısız olsa bile dış bootstrap `$LASTEXITCODE` kontrol etmediği için UI yanlış biçimde `Remote installer completed` gösterebiliyordu. Ayrıca başarısız servis başlangıcında rollback servisi sildiği için kalıcı tanılama kanıtı yetersizdi.

## R5 kalıcı düzeltmeleri
1. Child installer yalnız o proses için `-ExecutionPolicy Bypass` ile çalışır; makinenin kalıcı policy'si değiştirilmez.
2. Child exit code 0 değilse deployment kesin `failed` olur.
3. Kalıcı install logu `C:\ProgramData\YazmaBackup\Logs\meshcentral-install-<deployment-id>.log` altında korunur.
4. Remote success marker ancak Windows servisi `Running` ve enrollment kimliği + DPAPI access token dosyaları mevcutsa üretilebilir.
5. Enrollment evidence için 90 saniyelik bounded wait vardır; servis bu sırada düşerse anında hata üretilir.
6. Installer servis başlangıcında ek 5 saniye kararlılık doğrulaması yapar.
7. Servis sağlık hatasında `sc queryex`, son Service Control Manager olayları ve son Agent log kuyruğu ayrı `service-health-*.log` dosyasına yazılır.
8. Rollback korunur; ancak gerçek hata ve log yolu çağırana taşınır.
9. `RUN_SERVER.ps1` yeni win-x64 Agent ZIP'ini `dist` altında otomatik bulur.
10. VERIFY bu davranışların geri dönmesini engelleyen R5 invariantlarını kontrol eder.

## Windows kabul kapısı
```powershell
cd C:\YazmaBackup
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\VERIFY.ps1
.\scripts\PUBLISH_AGENT.ps1 -Runtime win-x64
.\scripts\RUN_SERVER.ps1
```

Gerçek hedefte beklenen durum: `queued -> dispatching -> installing -> succeeded`. Hedefte `YazmaBackupAgent` Running olmalı ve `C:\ProgramData\YazmaBackupgent.json` ile `agent-access-token.dpapi` bulunmalıdır.

`productionReady` bu saha kanıtı, NAS restore ve PostgreSQL runtime doğrulaması tamamlanana kadar `false` kalır.
