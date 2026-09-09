# R29.5 Repository Devre Kesici Kurtarma Probu

## Kök neden

`TestNasAccess` komutu repository kimliği taşıdığı için genel devre kesici kontrolüne giriyor ve açık devre sırasında daha çalışmadan `RepositoryCircuitOpenException` ile duruyordu. Böylece NAS erişimi düzeltilse bile gerçek test yaparak devreyi kapatmak mümkün değildi.

## Kalıcı düzeltme

- Yalnız `TestNasAccess` komutu açık repository devresinde kurtarma probu olarak çalışabilir.
- Backup, restore, scrub, health scan ve diğer veri operasyonları açık devrede fail-fast davranışını korur.
- Test gerçek NAS bağlantısı, klasör okuma ve geçici dosya yazma/silme işlemlerini gerçekleştirir.
- Test başarılı olursa mevcut `ReportSuccess(repositoryId)` akışı hata sayacını ve bekleme süresini atomik state yazımıyla sıfırlar.
- Test başarısız olursa transient hata yine `ReportFailure` yoluna girer; devre güvenliği gevşetilmez.

Bu değişiklik yedek verisini silmez, retention durumunu değiştirmez ve yalnız doğrulanmış başarılı erişimden sonra devreyi iyileştirir.

## Kullanıcı / bilgisayar bağlamı

Bilgisayarlar ekranındaki `Kullanıcı / Sahibi` değeri ortak `agentDisplayName` görünümüne bağlandı. Atanmış ad artık bilgisayar adıyla birlikte Politika, Operasyon geçmişi ve detayı, NAS test seçimi, Geri Yükleme, Doğrulama, Pilot ve Agent Yaşam Döngüsü seçimlerinde gösterilir. Atama yoksa mevcut bilgisayar adı tek başına kullanılmaya devam eder.
