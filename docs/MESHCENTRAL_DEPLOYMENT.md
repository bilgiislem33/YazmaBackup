# MeshCentral ile YazmaBackup v1.1.0 Dağıtımı

MeshCentral; Agent paketinin Windows cihaza ulaştırılması, SYSTEM/yönetici bağlamında kurulması, güncellenmesi ve sağlık kontrolü için kullanılır. **Backup veri trafiği MeshCentral üzerinden geçmez.**

```powershell
.\scripts\PUBLISH_AGENT.ps1 -Runtime win-x64
Get-FileHash .\dist\YazmaBackupAgent_v1.1.0_win-x64.zip -Algorithm SHA256

$grant = .\scripts\CREATE_ENROLLMENT_TOKEN.ps1 `
  -Server "https://backup.example.com" `
  -AccessToken $env:YAZMABACKUP_MANAGEMENT_TOKEN `
  -ValidForMinutes 15 -MaxUses 50
```

MeshCentral görevinde yalnız kısa ömürlü enrollment tokenı kullanın. Repository AES key, Management API Token veya break-glass kodunu MeshCentral script/history alanına koymayın.

```powershell
$env:YAZMABACKUP_ENROLLMENT_TOKEN = "SURELI_ENROLLMENT_TOKEN"
.\MESH_INSTALL_AGENT.ps1 `
  -PackageUrl "https://release.example.com/YazmaBackupAgent_v1.1.0_win-x64.zip" `
  -PackageSha256 "64_HEX_SHA256" `
  -Server "https://backup.example.com"
Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue
```

Kontrol:

```powershell
.\scripts\LIST_AGENTS.ps1 -Server "https://backup.example.com" -AccessToken $env:YAZMABACKUP_MANAGEMENT_TOKEN
```
