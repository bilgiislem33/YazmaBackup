# YazmaBackup v1.1.0 — Enterprise Validation & Self-Healing Fabric

YazmaBackup, Windows endpoint'lerden NAS/repository hedeflerine merkezi, artımlı, şifreli ve doğrulanabilir yedek alan kurumsal backup platformudur. v1.1.0'ın ana hedefi, v1.0.0 Enterprise Pilot Candidate'i gerçek pilot arızalarını ölçebilen ve düşük riskli sorunları güvenli biçimde düzeltebilen bir validation fabric'e taşımaktır.

## v1.1.0 büyük geçişi

- Derin Windows/NAS doğrulama matrisi
- Gerçek VSS snapshot create/read/cleanup kontrolü
- USN Journal capture ve full-scan fallback görünürlüğü
- 64 KiB NAS WriteThrough write/delete doğrulaması
- Repository encryption key ve circuit-breaker doğrulaması
- Granular dosya/klasör geri yükleme
- İzole Restore Sandbox + SHA-256 bütünlük doğrulaması
- Güvenli allowlist Self-Healing
- Modern light tema içinde **Doğrulama & Onarım** merkezi
- PostgreSQL migration 007 validation/self-healing sözleşmesi
- Windows PowerShell CLI: `DEEP_VALIDATION`, `GRANULAR_RESTORE`, `RESTORE_SANDBOX`, `SELF_HEAL`

Mevcut VSS/USN artımlı yedekleme, CDC/dedup, AES-256-GCM, ransomware protection, live transfer graph, easy restore, Recovery Plan/Runbook, ECDSA evidence, RBAC/OIDC, alarm merkezi, MeshCentral eşleme ve Pilot Center korunur.

## İlk Windows doğrulaması

```powershell
cd C:\Users\Gursoy_IT\Desktop\YazmaBackup_v1.1.0_ValidationSelfHealingFabric
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\VERIFY.ps1
```

`VERIFY.ps1` gerçek Windows üzerinde .NET 10 restore/build, warnings-as-errors, Enterprise SelfTest ve PowerShell 5.1 parse/BOM kapılarını çalıştırır.

## Control Plane

```powershell
.\scripts\GENERATE_SECRETS.ps1
$env:ASPNETCORE_URLS = "http://127.0.0.1:5088"
.\scripts\RUN_SERVER.ps1
```

Sonra tarayıcı:

```text
http://127.0.0.1:5088
```

## Derin validation CLI

```powershell
.\scripts\DEEP_VALIDATION.ps1 `
  -Server "http://127.0.0.1:5088" `
  -AccessToken $env:YAZMABACKUP_MANAGEMENT_TOKEN `
  -AgentId "<AGENT-GUID>" `
  -SourcePath "C:\Users\Kullanici\Documents" `
  -RepositoryRoot "\\NAS01\YazmaBackup" `
  -RepositoryId "NAS01-MERKEZ"
```

## Granular restore

```powershell
.\scripts\GRANULAR_RESTORE.ps1 `
  -Server "http://127.0.0.1:5088" `
  -AccessToken $env:YAZMABACKUP_MANAGEMENT_TOKEN `
  -AgentId "<AGENT-GUID>" `
  -RepositoryRoot "\\NAS01\YazmaBackup" `
  -RepositoryId "NAS01-MERKEZ" `
  -BackupId "<BACKUP-ID>" `
  -DestinationRoot "C:\YazmaBackup-Restore" `
  -IncludePaths "Belgeler/Teklif.xlsx","Muhasebe/2026"
```

## Restore Sandbox

```powershell
.\scripts\RESTORE_SANDBOX.ps1 `
  -Server "http://127.0.0.1:5088" `
  -AccessToken $env:YAZMABACKUP_MANAGEMENT_TOKEN `
  -AgentId "<AGENT-GUID>" `
  -RepositoryRoot "\\NAS01\YazmaBackup" `
  -RepositoryId "NAS01-MERKEZ" `
  -BackupId "<BACKUP-ID>" `
  -SandboxRoot "C:\YazmaBackup-Sandbox"
```

## Self-Healing

Önce teşhis:

```powershell
.\scripts\SELF_HEAL.ps1 `
  -Server "http://127.0.0.1:5088" `
  -AccessToken $env:YAZMABACKUP_MANAGEMENT_TOKEN `
  -AgentId "<AGENT-GUID>" `
  -SourcePath "C:\Users\Kullanici\Documents" `
  -RepositoryRoot "\\NAS01\YazmaBackup" `
  -RepositoryId "NAS01-MERKEZ" `
  -Mode Diagnose
```

Otomatik onarım yalnız kod içinde allowlist edilen düşük riskli eylemleri uygular. VSS/USN/NAS ACL veya ransomware lock gibi yüksek etkili ayarlar otomatik değiştirilmez.

## Üretim durumu

`VERSION.json` içinde `productionReady=false` kalır. v1.1.0 kaynak/mimari olarak Enterprise Pilot Validation Candidate'dir; gerçek Windows/VSS/USN + SMB NAS fault-matrix ve normalized PostgreSQL runtime tamamlanmadan GA ilan edilmez.

Ayrıntılar:

- `docs/VALIDATION_FABRIC.md`
- `docs/SELF_HEALING.md`
- `docs/GRANULAR_RESTORE_SANDBOX.md`
- `docs/PROGRESS.md`

## Agent update health-check + rollback

Pilot/üretim güncellemesinde `INSTALL_AGENT.ps1` için `-ManagementAccessToken` ve `-ExpectedAgentId` verilirse yeni Agent sürümü Control Plane'de yeni `agentVersion` heartbeat'i üretmeden güncelleme başarılı kabul edilmez. Health-check başarısızsa servis eski `ImagePath` ile geri alınır ve eski Agent klasörleri korunur.
