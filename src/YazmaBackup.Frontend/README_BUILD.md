# YazmaBackup Frontend Build Contract

R26'dan itibaren `YazmaBackup.Frontend` tek UI kaynağıdır.

Production publish için `package-lock.json` zorunludur ve `npm ci` kullanılır.
İlk R26 checkout'unda lock yoksa bir kez:

```powershell
cd src\YazmaBackup.Frontend
npm install --package-lock-only --ignore-scripts --no-audit --no-fund
cd ..\..
.\scripts\VERIFY.ps1
```

Oluşan `package-lock.json` kaynak paketine/depoya eklenmeden production publish kabul edilmez.
