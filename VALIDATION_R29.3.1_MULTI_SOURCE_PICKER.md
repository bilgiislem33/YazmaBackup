# R29.3.1 Çoklu Kaynak Klasör Seçimi

Politika formundaki doğrudan kaynak yol girişi kaldırıldı. Yönetici, seçili Agent'ın sürücülerini ve klasörlerini canlı olarak gezebilir; onay kutularıyla birden fazla klasörü aynı işlemde seçebilir.

## Güvenlik ve tutarlılık

- Tarayıcı yöneticinin bilgisayarını değil, seçili YazmaBackup Agent'ını gezdirir.
- Klasör listesi mevcut `BrowsePath` Agent komutu üzerinden alınır.
- En az bir kaynak seçilmeden politika oluşturulamaz.
- Aynı klasör büyük/küçük harf farkıyla iki kez seçilemez.
- API 1–100 benzersiz kaynak yolu kabul eder.
- Çoklu kaynak politikaları `CreateBackupPoliciesAsync` ile tek state commit içinde oluşturulur; kısmi oluşturma yapılmaz.
- Mevcut tek-kaynak politika ve zamanlayıcı modeli korunur: her seçili klasör ayrı, izlenebilir bir politika olur.

## Doğrulama

- `git diff --check`
- Merkezi Türkçe arayüz ve çoklu seçim invariant kontrolü
- ControlPlane route snapshot: `POST /api/v1/admin/policies/multi-source`
- CI üzerinde .NET testleri, TypeScript typecheck ve production frontend build

## R29.3.2 derleme düzeltmesi

İlk R29.3.1 paketinde politika ekranı, geri yükleme bileşeninin yerel kapsamındaki `Enqueue` tipine başvuruyordu. `EnqueueResponse` ortak frontend kapsamına taşındı; klasör gezgini, geri yükleme ve doğrulama işlemleri aynı sözleşmeyi kullanacak şekilde birleştirildi. Statik kalite kapısı eski `api<Enqueue>` kullanımını artık reddeder.
