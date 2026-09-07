# YazmaBackup v1.2.0 — MeshCentral Fleet Connector

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
cd C:\Users\Gursoy_IT\Desktop\YazmaBackup_v1.2.0_MeshCentralFleetConnector
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


## Yönetim API Token ve repository key akışı

İlk girişte bootstrap administrator parolası değiştirildikten sonra CLI işlemleri için kalıcı AdminKey yerine süreli **Management API Token** kullanılır. Token değeri kaynak dosyalarına veya scriptlere gömülmez.

```powershell
.\scripts\CREATE_MANAGEMENT_API_TOKEN.ps1 `
  -Server "http://127.0.0.1:5088" `
  -Username "admin" `
  -Name "Gursoy-IT-CLI" `
  -Roles administrator `
  -ValidForHours 24
```

Script tokenı yalnız bir kez gösterir ve ekranda çalıştırabileceğiniz şu biçimde bir satır üretir:

```powershell
$env:YAZMABACKUP_MANAGEMENT_TOKEN = 'ybmt_...'
```

Bu satırı aynı PowerShell penceresinde çalıştırın. Tokenı README, `.ps1`, MeshCentral görev geçmişi veya kaynak koda yapıştırmayın.

Repository AES-256 anahtarı düz metin dosyaya yazılmaz. `GENERATE_REPOSITORY_KEY.ps1`, anahtarı **DPAPI-CurrentUser** ile korunan `.ybkey` dosyasına yazar.

```powershell
$key = .\scripts\GENERATE_REPOSITORY_KEY.ps1 `
  -RepositoryId "NAS01-MERKEZ"

.\scripts\PROVISION_REPOSITORY_KEY_REMOTE.ps1 `
  -Server "http://127.0.0.1:5088" `
  -AccessToken $env:YAZMABACKUP_MANAGEMENT_TOKEN `
  -AgentId "<AGENT-GUID>" `
  -RepositoryId "NAS01-MERKEZ" `
  -KeyId $key.keyId `
  -KeyFile $key.keyFile `
  -MakeActive
```

Remote provision sırasında `.ybkey` yalnız yerel yönetici oturumunda DPAPI ile açılır; Control Plane komut kuyruğuna ham repository anahtarı bırakılmaz. Agent public key mevcutsa key materyali Agent için RSA-OAEP-SHA256 ile sarılır. MeshCentral görev geçmişine Management API Token, `.ybkey` içeriği veya repository key materyali koymayın.

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

## R1 VERIFY senkronizasyon düzeltmesi

Gerçek Windows doğrulamasında `[4/23] Data Plane güvenlik invariantları` adımı, RSA-OAEP-SHA256 uygulamasını yanlışlıkla `AgentWorker.cs` içinde arıyordu. Gerçek uygulama `AgentKeyExchangeStore.cs` içindedir. R1, kalite kapısını gerçek sahip sınıfa bağlar ve restore-drill kontrolünü ayrı tutar. Üretim key-wrap davranışı değiştirilmemiştir. Aynı düzeltme v1.2 geliştirme dalına da taşınmıştır.

## R2 doğrulayıcı senkronizasyonu
Windows `VERIFY.ps1` OIDC kalite kapısındaki kırılgan literal `issuer + "\\n" + subject` araması kaldırıldı. OIDC artık issuer/sub claim binding ve SHA-256 issuer+subject fallback kimliği semantik olarak ayrı ayrı doğrulanır. Ürün davranışı değişmedi; bu R2 yalnız doğrulayıcının gerçek v1.1 OIDC uygulamasıyla senkronizasyonudur.


## R3 Sentinel VERIFY senkronizasyonu

Windows VERIFY [8/23] kontrolündeki stale `restore-drill-failed` literal araması kaldırıldı. Sentinel gerçek davranışı semantik olarak `AgentCommandType.RestoreDrill` + `"restore-drill"` kategori + ortak `{category}-failed` fingerprint bileşimi üzerinden doğrulanır.

## R4 Validation Center VERIFY senkronizasyonu

Windows VERIFY [12b/23] kontrolü `Validation Center UI eksik.` hatası veriyordu. Kök neden ürün kodunda değil doğrulayıcıdaydı: `wwwroot/index.html`, `app.js` ve `styles.css` BOM'suz UTF-8 kaydedilmiş, Windows PowerShell 5.1 `Get-Content -Raw` bu dosyaları `-Encoding` belirtilmeden okurken BOM yokluğunda sistemin ANSI kod sayfasına düşüyor ve Türkçe çok baytlı karakterleri (`Doğrulama & Güvenli Onarım Merkezi` içindeki ğ/ü/ı) yanlış çözüyordu. R4, `[9/23]` adımında `$html`/`$js`/`$css` okumalarına açık `-Encoding UTF8` ekleyerek bunu BOM'dan bağımsız hale getirir. Ürün davranışı değişmedi; tarayıcıya sunulan `index.html` zaten doğru UTF-8 baytlarıyla servis ediliyordu. Aynı düzeltme v1.2 geliştirme dalına da taşınmıştır.

## R5 Release build düzeltmesi

R1-R4'ten farklı olarak bu round `VERIFY.ps1` [14/23] Release build adımında gerçek Windows .NET 10 derleyici/analiz hatalarını düzeltir (23 hata: `YazmaBackup.Infrastructure` 5, `YazmaBackup.ControlPlane` 18) — bu ortamda .NET 10 SDK bulunmadığından bu hatalar daha önce hiç yakalanamamıştı. Düzeltmeler `CA1001` (eksik `IDisposable`), `CA1512`/`CA1822` (modern API + gereksiz örnek üyesi), `CA1838` (P/Invoke `StringBuilder` → `char[]`), `CA1068` (`CancellationToken` son parametre olmalı), `CS0136` (aynı blokta çakışan yerel değişken adı), `CA1716` (`error` parametre adı diğer dillerde ayrılmış anahtar sözcükle çakışıyor), `CA1848`/`CA1873` (doğrudan `ILogger` çağrıları yerine kaynak-üretimli `[LoggerMessage]`), `CA1859` (arayüz yerine somut dönüş tipi) kurallarını kapsar. Hepsi noktasal ve davranış-koruyucu; Sentinel/OIDC/restore-drill iş mantığı değişmedi. Ayrıntı: `VALIDATION_R5.md`.

## R6 using düzeltmesi

R5'teki `StringBuilder` → `char[]` P/Invoke değişikliği sırasında `WindowsUsnJournalChangeTracker.cs`'den `using System.Text;` yanlışlıkla kaldırıldı; aynı dosyada `Encoding.Unicode`/`Encoding.UTF8` hâlâ kullanılıyordu ve bu ortamda gerçek derleme yapılamadığından fark edilmedi. Gerçek Windows build'i bunu `CS0103` olarak yakaladı. R6 yalnızca o using satırını geri ekler; başka hiçbir değişiklik yok. `YazmaBackup.ControlPlane`'in R5'teki 18 hatası bu turda gerçek derleyicide temiz geçti. Ayrıntı: `VALIDATION_R6.md`.

## R7 Unsafe blocks / CA1862 düzeltmesi

R6 sonrası Infrastructure ve ControlPlane temiz derlendi; bu ilk kez `YazmaBackup.Agent` ve `YazmaBackup.SelfTest`'in derleyiciye ulaşmasını sağladı ve önceden hiç görülmemiş 4 hata çıktı — R5/R6'da değişen dosyalarla ilgisiz, sırf bu iki proje ilk kez derlendiği için ortaya çıktılar. `RepositoryHealthScanner.cs`'teki `[LibraryImport]` tabanlı `GetDiskFreeSpaceExW` çağrısı unsafe bağlam gerektiriyordu (`SYSLIB1062` + 2×`CS0227`) → `YazmaBackup.Agent.csproj`'a `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` eklendi. `SelfTest/Program.cs:563`'teki `.ToLowerInvariant() ==` kalıbı (`CA1862`) → `string.Equals(..., StringComparison.OrdinalIgnoreCase)`'e çevrildi. Ayrıntı: `VALIDATION_R7.md`.


## R8 Agent Release Build düzeltmesi

Gerçek Windows .NET 10 derlemesinde `YazmaBackup.Agent` projesi warnings-as-errors nedeniyle 23 hata verdi. R8 bu hataları bastırmak yerine kök tasarım düzeltmeleriyle temizler:

- `AgentWorker`: `WindowsSessionActivityProbe.IsUserActive` static çağrısı düzeltildi; `YazmaBackup.Domain` import edilerek `ProtectionPolicy` ve `BackupManifest` tipleri çözüldü; `Browse` statik hale getirildi.
- `AgentIdentityStore`, `AgentConfigStore`, `CommandExecutionJournal`: gerçekten stateless oldukları için static store modeline geçirildi.
- `MachineSecretStore`: analyzer bastırması yerine gerçek state taşıyan Windows DPAPI service olarak düzenlendi; machine-scope davranış korunur.
- `AgentIdentityStore` ve `AgentUpdateStager`: tekrar tekrar `JsonSerializerOptions` oluşturmak yerine cache'li seçenek kullanır.
- `ProtectionLockStore` ve `AgentUpdateStager`: Windows platform sınırı açık `[SupportedOSPlatform("windows")]` sözleşmesine bağlandı.
- `DeepValidationProbe`: analyzerın istediği somut `List<ValidationCheckDto>` kullanımı.
- `RepositoryKeyStore`: private key-ring koleksiyonu diziye çevrilerek gereksiz `IReadOnlyList` soyutlaması kaldırıldı.

Bu revizyon backup/restore formatını, repository schema'yı veya Control Plane API sözleşmesini değiştirmez. `VERSION.json` ürün sürümü `1.1.0`, release revision `r8` olarak kalır.

## R10 SelfTest izolasyonu + R9 DPAPI description doğrulaması

Gerçek Windows .NET 10 Release build'de R8 sonrası yalnız `MachineSecretStore.Unprotect` için `CA1822` kaldı. Metot analyzer'ı susturmak için yapay biçimde static yapılmadı. Bunun yerine DPAPI koruma sırasında kullanılan `_dataDescription`, `CryptUnprotectData` çıktısından geri okunup sabit-zaman gerektirmeyen exact ordinal karşılaştırmayla doğrulanır. Böylece metot gerçek instance state kullanır, eski YazmaBackup DPAPI secret'larıyla uyumluluk korunur ve başka açıklama ile üretilmiş DPAPI blob'ları reddedilir. Aynı düzeltme v1.2 geliştirme dalına da taşındı.


### R10 gerçek Windows SelfTest izolasyon düzeltmesi

R9 Windows doğrulamasında .NET 10 restore ve 8/8 Release/warnings-as-errors build PASS oldu. SelfTest state-schema migration senaryosu process seviyesinde kalmış `YAZMABACKUP_STATE_DIR` değerinden etkilenebiliyordu. R10 bu testi kendi state dizinine scope eder, future-schema alt testinde ayrı state dizinine geçer ve test sonunda önceki environment değerini `finally` ile geri yükler. Ürün StateStore migration davranışı değiştirilmez.

## R11 Documentation Sync

Gerçek Windows doğrulamasında .NET 10 `Release` build 8/8 proje için warnings-as-errors ile PASS ve Enterprise SelfTest PASS oldu. `[19/23] Doküman sözleşmesi`, README içinde güncel Management API Token + DPAPI `.ybkey` repository-key provision akışını zorunlu tutuyordu; ürün kodu doğru olmasına rağmen README bu operasyon sırasını göstermiyordu. R11 README'yi gerçek güvenlik modeliyle senkronlar ve sonraki doküman drift'ini release kapısında tutar.

## v1.2.0 MeshCentral Fleet Connector

Yeni `MeshCentral` yönetim merkezi doğrudan MeshCentral Fleet envanterini senkronize eder, hostname tabanlı güvenli Agent eşleştirmesi yapar ve Agent eksik çevrimiçi cihazlara tek tık dağıtım başlatır. Kurulum ve güvenlik ayrıntıları için `docs/MESHCENTRAL_FLEET_CONNECTOR.md` dosyasına bakın.

## v1.2.0 R4
MeshCentral tek-tık Agent dağıtımı HTTP 202 + background worker mimarisine geçirildi. Gerçek saha sözleşmesi olan `RunCommand --run` zorunlu hale getirildi; 504 bekleme, sahte `dispatched`, state-path drift ve VERIFY generated-artifact self-fail sorunları kalıcı olarak ele alındı. Ayrıntılar: `VALIDATION_R4.md`.


## v1.2.0 R5
MeshCentral uzaktan Agent kurulumunda saha testlerinde görülen Execution Policy ve child-installer exit-code hataları kalıcı olarak kapatıldı. Uzak kurulum artık yalnız proses seviyesinde `ExecutionPolicy Bypass` kullanır, gerçek child exit code'u zorunlu başarı kanıtı sayar, kalıcı install/service diagnostic logları bırakır ve servis + local enrollment evidence oluşmadan success marker üretmez. `RUN_SERVER.ps1` en güncel win-x64 Agent ZIP'ini `dist` altında otomatik keşfeder. Ayrıntılar: `VALIDATION_R5.md`.

## v1.2.0 R5.1
Gerçek saha dağıtımında görülen `Windows servisi oluşturulamadı` hatası için LocalSystem service creation `New-Service` tabanına taşındı. Delayed-auto registry ile açıkça uygulanır, create retry + strict SCM/sc.exe diagnostics + ImagePath doğrulaması eklendi. Ayrıntılar: `VALIDATION_R5.1.md`.

## R5.6 installer reliability
R5.6 makes Windows ACL recovery deterministic under Windows PowerShell 5.1. `takeown.exe`/`icacls.exe` stderr is captured via `System.Diagnostics.ProcessStartInfo` instead of PowerShell's native stderr adapter, so best-effort `/T /C` recovery can no longer be converted into a terminating `NativeCommandError` before YazmaBackup evaluates the process exit code. The strict root ACL and real write/read/delete preflight remain authoritative.


## R5.7 Windows service configuration reliability
R5.7 removes `sc.exe config/create/delete` from the Agent install/update/rollback path. Existing service changes are applied through `Win32_Service.Change` with named typed WMI parameters, gMSA creation uses `Win32_Service.Create`, and default LocalSystem creation continues to use `New-Service`. This closes the field-observed `sc.exe` ExitCode 1639 failure caused by quoted `Program Files` ImagePath parsing while preserving strict ImagePath, startup, service-health and rollback validation.


### v1.2.0 R5.8
MeshCentral deployment tamamlanması artık heartbeat-driven'dır: Agent ilk başarılı heartbeat'ini gönderdiğinde unique node eşleşmesi doğrudan `succeeded` olur. `installing` kayıtlar için 10 saniyelik reconciliation fallback da vardır.
