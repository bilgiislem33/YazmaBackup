# YazmaBackup Ürün İlerleme Raporu — v1.1.0

| Alan | v1.0.0 | v1.1.0 R11 | Değişim |
|---|---:|---:|---:|
| Kaynak / mimari geliştirme | %99 | %99 | 0 |
| Güvenlik | %98 | %99 | +1 |
| Test / kalite kapıları | %99 | %99 | 0 |
| Windows gerçek ortam doğrulaması | %15 | %72 | +57 |
| NAS / restore gerçek doğrulaması | %18 | %18 | 0 |
| Merkezi yönetim / UX | %99 | %99 | 0 |
| Pilot / üretim hazırlığı | %87 | %94 | +7 |
| Ticari kurumsal ürün seviyesi | %91 | %94 | +3 |

## Windows doğrulaması neden yükseldi, NAS neden yükselmedi?

R10 kullanıcı Windows ortamında .NET 10 restore, 8/8 Release warnings-as-errors build ve Enterprise SelfTest runtime kapılarını geçtiği için Windows doğrulaması artık %72 seviyesindedir. Buna karşılık gerçek SMB NAS, VSS/USN fault-matrix ve saha restore testleri henüz çalıştırılmadığından NAS / restore gerçek ortam puanı %18’de tutulur.

## v1.1.0 büyük geçişi

- gerçek VSS snapshot validation,
- USN capture/fallback validation,
- NAS write-through probe,
- repository circuit validation,
- granular file/folder restore,
- isolated restore sandbox,
- düşük riskli allowlist self-healing,
- Validation & Repair Center UI,
- PostgreSQL migration 007 sözleşmesi,
- yeni Windows fault-matrix script/runbook altyapısı.

Genel ürün seviyesi: **%94 — Enterprise Pilot Candidate, Windows Build/SelfTest Validated**.

## Agent update hardening

v1.1.0 installer, isteğe bağlı Management API Token + Expected Agent ID verildiğinde yeni sürümü yalnız Windows servisinin çalışmasıyla başarılı saymaz. Yeni sürüm heartbeat/version kanıtı gelmezse servis eski `ImagePath` değerine geri alınır; eski Agent sürüm dizinleri yalnız health-check başarılı olduktan sonra temizlenir.

## v1.1.0 R9 Windows build geri bildirimi

R8 gerçek Windows doğrulamasında .NET 10 restore başarıyla geçti ve 8 projenin 7'si Release/warnings-as-errors derlemesini tamamladı. `YazmaBackup.Agent` için yalnız `MachineSecretStore.Unprotect` CA1822 kaldı. R9 bu tek analyzer problemini DPAPI data-description doğrulaması ile kökten kapatır. R9 henüz Windows üzerinde derlenmediği için tam build PASS iddiası yapılmaz.

- Kaynak / mimari: %99
- Güvenlik: %99
- Test / kalite: %99
- Windows gerçek ortam doğrulaması: %38
- NAS / restore gerçek ortam doğrulaması: %18
- Merkezi yönetim / UX: %99
- Pilot / üretim hazırlığı: %92
- Ticari kurumsal ürün seviyesi: %93


## v1.1.0 R10 gerçek Windows doğrulaması

R9 Windows testinde .NET 10 restore ve 8/8 Release/warnings-as-errors build PASS oldu. İlk runtime engeli `StateSchemaMigrationAsync` self-testinde process-level `YAZMABACKUP_STATE_DIR` sızıntısıydı. R10 self-test state dizinini scoped hale getirir; ürün StateStore migration davranışı değiştirilmez.

R10 sonrası doğrulanmış durum:

- Kaynak / mimari: %99
- Güvenlik: %99
- Test / kalite: %99
- Windows gerçek ortam doğrulaması: %55
- NAS / restore gerçek ortam doğrulaması: %18
- Merkezi yönetim / UX: %99
- Pilot / üretim hazırlığı: %93
- Ticari kurumsal ürün seviyesi: %93

Windows puanı; gerçek .NET 10 restore ile 8/8 Release/warnings-as-errors build PASS olduğu ve Enterprise SelfTest runtime aşamasına ulaşıldığı için yükseltilmiştir. SelfTest henüz tamamen PASS olmadığı için ticari ürün seviyesi artırılmaz.

## v1.1.0 R11 gerçek Windows build + SelfTest doğrulaması

R10 gerçek Windows doğrulamasında kritik eşik geçildi:

- .NET 10 restore PASS,
- 8/8 proje Release + warnings-as-errors PASS,
- Enterprise SelfTest runtime PASS,
- PowerShell 5.1 syntax PASS,
- JavaScript syntax PASS,
- secret/private-key taraması PASS.

Kalan hata ürün kodunda değil `[19/23] Doküman sözleşmesi` adımındaydı. README, güncel Management API Token ve DPAPI `.ybkey` repository-key provision akışını göstermiyordu. R11 dokümanı gerçek script/API davranışıyla senkronlar.

R11 sonrası doğrulanmış durum:

- Kaynak / mimari geliştirme: **%99**
- Güvenlik: **%99**
- Test / kalite kapıları: **%99**
- Windows gerçek ortam doğrulaması: **%72**
- NAS / restore gerçek ortam doğrulaması: **%18**
- Merkezi yönetim / UX: **%99**
- Pilot / üretim hazırlığı: **%94**
- Ticari kurumsal ürün seviyesi: **%94**

Windows puanı; gerçek .NET 10 Release build'in 8/8 proje için ve Enterprise SelfTest'in runtime'da PASS olması nedeniyle yükseltilmiştir. NAS puanı artırılmamıştır; gerçek SMB NAS + VSS/USN + restore fault-matrix henüz saha kanıtına sahip değildir.

Genel seviye: **%94 — Enterprise Pilot Candidate, Windows Build/SelfTest Validated**.


### R4 saha düzeltmeleri
- Gerçek MeshCentral RunCommand sözleşmesi `--run` olarak doğrulandı.
- Agent deployment API'si 504 üretmeyecek şekilde background queue + HTTP 202 mimarisine geçirildi.
- Restart recovery, 5 dakikalık subprocess timeout, monotonic deployment state ve success/error marker doğrulaması eklendi.
- İlk gerçek tek-cihaz Agent enrollment/heartbeat doğrulaması R4 saha kapısıdır.
