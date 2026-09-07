# YazmaBackup v1.1.0 Protection Plane

## Amaç

Protection Plane'in görevi ransomware şüphesinde şifrelenmiş/kitle halinde değiştirilmiş veriyi yeni “başarılı” restore point olarak commit etmeden önce durdurmaktır. Sistem antivirüs/EDR yerine geçmez; backup bütünlüğü için ek bir koruma katmanıdır.

## Sinyaller

`RansomwareProtectionGuard` aşağıdaki sinyalleri birlikte kullanır:

1. Önceki restore point'e göre değişen/silinen/yeni dosya oranı.
2. Önceki path'e yeni bir uzantı eklenmiş rename paterni (`rapor.xlsx` → `rapor.xlsx.locked`).
3. Değişen dosyalardan sınırlı sayıda örnekte Shannon entropy.
4. Doğal olarak yüksek entropy taşıyan ZIP/7z/JPEG/PNG/PDF/Office Open XML gibi formatların entropy sinyalinden hariç tutulması.

Tek bir sinyal varsayılan olarak incident üretmez. Toplu uzantı değişimi + yüksek entropy veya çok yüksek toplu rewrite + yüksek entropy kombinasyonu gerekir.

## Fail-closed davranış

Şüpheli assessment eşik aşarsa:

- yeni manifest yazılmaz;
- USN checkpoint commit edilmez;
- mevcut temiz restore point'ler değişmez;
- `AutoLockOnDetection=true` ise Agent DPAPI korumalı incident lock oluşturur;
- sonraki `BackupPath` komutları incident temizlenene kadar reddedilir;
- restore, scrub ve güvenli yönetim işlemleri kullanılabilir kalır.

## Incident temizleme

Incident lock yalnız `ClearProtectionLock` komutu ile kaldırılır. Komut isteğe bağlı değil, UI/CLI tarafından aktif `IncidentId` ile gönderilir. Farklı incident ID verilirse Agent kilidi açmaz. Bu işlem Security Administrator/Administrator yetkisindedir.

## Yanlış pozitif yönetimi

Eşikler üretimde pilot gözlemle ayarlanmalıdır. Büyük toplu migration, yazılım build output'u, medya transcoding veya arşivleme işleri korunan klasörde yapılıyorsa ayrı policy/klasör tasarımı tercih edilmelidir. Koruma tamamen kapatılabilir, ancak kurumsal policy varsayılanı açıktır.

## Değişmezlik

`RetentionPolicy.ImmutabilityHours` varsayılan 24 saattir. Retention bu yaştan yeni restore point'i silmez. Repository manifestleri aynı `BackupId` altında farklı içerikle overwrite edilemez. Bunlar YazmaBackup yazılım katmanı korumalarıdır; NAS üreticisinin WORM/Object Lock garantisi değildir.

## USN kullanılamadığında güvenli fallback

USN Journal değişiklik seti güvenilir değilse protection preflight yalnız dosya boyutu ve zaman damgasına güvenmez. Metadata aynı görünse bile önceki manifestteki dosya SHA-256 ile mevcut dosyanın SHA-256 değeri karşılaştırılır. Bu, zaman damgasını koruyan veya yeniden yazdıktan sonra eski zamana çeken zararlı davranışlarının tam-tarama modunda sessizce kaçmasını önler. Bu yol performans optimizasyonu değil, fail-safe doğruluk yoludur.
