# YazmaBackup v1.2.0 R5.4 Validation

## Amaç
R5.3 saha testinde görülen `icacls.exe : C:\ProgramData\YazmaBackup\\*: Erişim engellendi.` hatasını recursive-first ACL onarımından çıkarıp root-first, wildcard-free ve kanıta dayalı bir recovery zinciriyle kalıcı kapatmak.

## Statik kabul kapıları
- `Set-PrivateAclOnRoot` recursive ACL işleminden önce çağrılır.
- Kök üzerinde `takeown /F <path>` + non-recursive ACL reset/inheritance removal/SYSTEM+Administrators grant/SYSTEM owner uygulanır.
- Kaynakta ProgramData wildcard `\*` ACL recovery kalıbı bulunmaz.
- Recursive child recovery `AllowNonZero` ile tanı toplar; nihai karar gerçek create/write/read/delete probe ile verilir.
- Kritik identity dosyaları ayrı ayrı ownership/ACL normalizasyonundan geçer.
- `ProgramData ACL preflight başarısız` mesajı aşama exit-code ve ACL tanılarını taşır.

## Windows saha kabulü
1. Önceki başarısız kurulumdan kalan bozuk ProgramData ACL ile MeshCentral üzerinden Agent Kur.
2. Deployment `queued -> dispatching -> installing -> succeeded` olmalı.
3. `Get-Service YazmaBackupAgent` => `Running`.
4. `agent.json` ve `agent-access-token.dpapi` mevcut olmalı.
5. `icacls C:\ProgramData\YazmaBackup` SYSTEM ve Administrators Full Control göstermeli.
6. Failure durumunda UI genel Access Denied yerine R5.4 preflight aşama tanısını göstermeli.

## Durum
Container tarafında ZIP/source-manifest/statik invariant kontrolleri yapılabilir. Gerçek Windows PowerShell/.NET/SCM saha doğrulaması kullanıcı ortamında yapılmalıdır.
