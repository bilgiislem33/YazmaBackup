# YazmaBackup v1.1.0 R9 MachineSecretStore Build Fix — Doğrulama

## Gerçek Windows bulgusu

R8, gerçek Windows .NET 10 Release build'de 8 projeden 7'sini başarıyla derledi. `YazmaBackup.Agent` içinde yalnızca `MachineSecretStore.Unprotect` için `CA1822` kaldı. Bu R9 paketi yalnız bu son analyzer sınıfını kökten düzeltir.

## Kök düzeltme

`Unprotect` metodu yapay biçimde `static` yapılmadı. `CryptProtectData` sırasında kullanılan `_dataDescription`, `CryptUnprotectData` çıktısından geri okunup `StringComparison.Ordinal` ile doğrulanır. Böylece:

- `Unprotect` gerçek instance state kullanır ve `CA1822` doğal olarak kapanır.
- `Read` de instance davranışını korur; yeni bir CA1822 zinciri oluşturulmaz.
- Önceki YazmaBackup DPAPI blob'ları aynı data description ile uyumlu kalır.
- Farklı description ile üretilmiş DPAPI blob'ları reddedilir.
- Analyzer suppression / pragma eklenmemiştir.

## Statik R9 doğrulaması

- R9 odaklı kaynak kontrolleri: 90/90 PASS
- JavaScript `node --check`: PASS
- .NET proje sayısı: 8/8
- ProjectReference yolları: PASS
- JSON parse: PASS
- XML/csproj parse: PASS
- PowerShell UTF-8 BOM: PASS
- Harici PackageReference: 0
- `VERSION.json.releaseRevision`: `r9`

## Gerçek Windows kapısı

Bu çalışma ortamında .NET 10 SDK bulunmadığından R9 Release build sonucu burada geçmiş sayılmaz. Nihai kanıt Windows makinede `scripts\VERIFY.ps1` içindeki:

- `[14/23] Release build / warnings-as-errors`
- `[15/23] Enterprise self-test`

adımlarının PASS sonucudur.

`productionReady=false` kalır; gerçek VSS/USN/SMB NAS pilotu tamamlanmadan GA ilan edilmez.
