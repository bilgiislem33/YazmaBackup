# R29.7 Kullanıcı Adına Göre Otomatik Yedekleme Hedefi

## Kalıcı isim düzeltmesi

Bilgisayarlar ekranı `assigned-user` endpointine yanlışlıkla `POST` gönderiyordu; sözleşme `PUT` olduğu için HTTP 405 oluşuyor ve yazılan isim yalnız yerel formda kalıyordu. İstek `PUT` olarak düzeltildi. Başarılı cevap ana konsolun ortak Agent state'ine anında uygulanır; sayfa yenilemeden Politika, NAS, Operasyon ve diğer seçimlerde görünür.

## NAS profilini otomatik devralma

Yedekleme Oluştur ekranı global NAS profilinden:

- depo kimliğini,
- ana NAS paylaşım yolunu

otomatik alır. Kullanıcı bu alanları her politikada yeniden yazmaz. Alanlar bilgi amaçlı salt okunur gösterilir; global profil yoksa kullanıcı Depolama & NAS ekranına yönlendirilir.

NAS profili kaydedildiğinde ana konsol state'i de anında güncellenir; kullanıcı sayfayı yenilemeden Yedekleme Oluştur ekranına geçtiğinde yeni hedef hazır gelir.

## Kullanıcı/bilgisayar klasörü

Seçilen bilgisayar için atanmış ad varsa bu ad, yoksa gerçek makine adı kullanılır:

```text
\\10.218.177.33\PC_Yedek\Erdem Gözlü
```

Windows klasör adında kullanılamayan karakterler güvenli biçimde `-` karakterine çevrilir. Bu kural yalnız frontend önizlemesi değildir: ControlPlane çoklu politika endpointi kayıtlı NAS profilini ve güncel Agent sahip bilgisini tekrar okuyarak hedef yolu sunucu tarafında üretir. İstemcinin farklı repository yolu göndermesi sonucu değiştiremez.

Repository motoru ilk yedekleme sırasında hedef klasörü, `chunks`, `manifests` ve quarantine yapısıyla birlikte otomatik oluşturur.
