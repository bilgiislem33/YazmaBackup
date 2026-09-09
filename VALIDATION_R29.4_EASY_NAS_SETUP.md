# R29.4 Kolay NAS Kurulumu ve Gerçek Bağlantı Testi

## Kullanıcı deneyimi

- Belirsiz kutular yerine `Depo adı`, `NAS paylaşım yolu`, `NAS kullanıcı adı` ve `NAS parolası` etiketleri gösterilir.
- `Merkez NAS` gibi doğal bir depo adı güvenli `Merkez-NAS` repository kimliğine otomatik dönüştürülür.
- UNC yolu yazılırken biçim hatası alanın hemen altında açıklanır.
- Kayıtlı parola kullanıcıya geri gönderilmez; değişmeyecekse boş bırakılabilir.
- Testin çalışacağı Agent/bilgisayar kullanıcı tarafından seçilir.

## Gerçek test

`Kaydet ve Bağlantıyı Test Et` işlemi global profili kaydeder, kimlik bilgisini Agent'lara güvenli biçimde kuyruğa alır ve seçili Agent üzerinde mevcut `TestNasAccess` komutunu çalıştırır. Sonuç ekranı ayrı ayrı şunları gösterir:

- Kimlik bilgisi yapılandırıldı mı?
- NAS klasörü okunabiliyor mu?
- Geçici dosya yazma ve silme denemesi başarılı mı?

Test sonucu Agent komut sonucu tamamlanana kadar beklenir; yalnız istemci tarafı biçim kontrolü başarı sayılmaz.

## Kalite kilidi

Türkçe arayüz kontrolü birleşik kaydet/test düğmesini, gerçek NAS endpointini, komut sonucu polling akışını ve okuma/yazma sonuç alanlarını zorunlu tutar.
