# YazmaBackup v1.1.0 R11 — Documentation Sync Validation

## Gerçek Windows kanıtı

2 Eylül 2026 tarihli kullanıcı Windows PowerShell doğrulamasında R10 paketi için:

- .NET 10 restore: PASS
- Release build / warnings-as-errors: 8/8 proje PASS
- YazmaBackup.Agent: PASS
- YazmaBackup.ControlPlane: PASS
- YazmaBackup.SelfTest: PASS build
- Enterprise SelfTest runtime: PASS
- PowerShell 5.1 syntax: PASS
- JavaScript syntax: PASS
- Secret/private-key taraması: PASS
- Doküman sözleşmesi: FAIL — README güncel Management API Token + `.ybkey` repository-key akışını göstermiyordu

R11 bu son doküman drift'ini giderir; ürün çalışma kodu veya repository formatı değişmez.

## R11 düzeltmesi

README artık gerçek operasyon sırasını açıkça içerir:

1. interaktif Administrator oturumuyla süreli Management API Token üretimi,
2. tokenın tek seferlik environment satırı olarak alınması,
3. DPAPI-CurrentUser korumalı `.ybkey` repository key oluşturulması,
4. `-KeyFile $key.keyFile` ile remote repository-key provision,
5. Agent tarafına RSA-OAEP-SHA256 wrapped key teslimi,
6. MeshCentral/job history içine token/key materyali yazmama uyarısı.

## R11 statik doğrulama

- `[19/23]` doküman sözleşmesi: PASS
- `[20/23]` operasyon scriptleri: PASS
- `[21/23]` sürüm / proje tutarlılığı: PASS
- `[22/23]` SelfTest senaryo sözleşmeleri: PASS
- 8 proje mevcut: PASS
- 14 ProjectReference yolu: PASS
- Legacy AdminKey CLI header kalıntısı: YOK
- README `Management API Token`: PASS
- README `-KeyFile $key.keyFile`: PASS
- VERSION.json: version 1.1.0 / stateSchema 10 / productionReady false / releaseRevision r11

## Üretim durumu

`productionReady=false` korunur. Gerçek Windows build ve Enterprise SelfTest artık doğrulanmıştır; ancak gerçek VSS/USN/SMB NAS fault-matrix, gerçek restore saha testi ve normalized PostgreSQL runtime tamamlanmadan GA ilan edilmez.
