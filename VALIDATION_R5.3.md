# YazmaBackup v1.2.0 R5.3 Validation

## Amaç
R5.3, gerçek saha dağıtımında görülen `UnauthorizedAccessException: C:\ProgramData\YazmaBackup\agent.json` hatasını kalıcı olarak kapatır.

## Kök neden sınıfı
Önceki başarısız/yarım kurulumlardan kalan ACL/owner sapması, explicit DENY veya tek taraflı identity/token kanıtı Agent bootstrap'ın kimlik dosyasını güvenli şekilde oluşturmasını engelleyebilir.

## Kalıcı düzeltmeler
- Installer `ProgramData\YazmaBackup` ağacını bootstrap öncesi sahiplik ve ACL açısından self-heal eder.
- ACL recursive reset edilir; inheritance kaldırılır; SYSTEM + local Administrators Full Control uygulanır; owner SYSTEM yapılır.
- Bootstrap öncesi gerçek create/write/delete probe'u zorunludur.
- Enrollment token mevcutken identity/token çiftinin yalnız bir üyesi varsa stale çift birlikte temizlenir.
- Mevcut tam identity/token çifti upgrade/reinstall senaryosunda korunur.

## Windows saha kabulü
1. `scripts\VERIFY.ps1` 23/23 PASS.
2. R5.3 `PUBLISH_AGENT.ps1 -Runtime win-x64` ile yeni Agent ZIP oluşturulur.
3. MeshCentral Agent Kur dağıtımı `queued -> dispatching -> installing -> succeeded` olur.
4. Hedefte `Get-Service YazmaBackupAgent` Running döner.
5. `C:\ProgramData\YazmaBackup\agent.json` ve `agent-access-token.dpapi` vardır.
6. `icacls C:\ProgramData\YazmaBackup` çıktısında SYSTEM ve BUILTIN\Administrators Full Control görülür.

## Not
Linux build ortamında Windows PowerShell/.NET saha davranışı doğrulanamaz; nihai build ve runtime kapısı Windows VERIFY + gerçek endpoint testidir.
