# Kurulum & Pilot Merkezi — v1.1.0

## Amaç

Backup uzmanı olmayan bir yöneticinin bir bilgisayarı güvenli pilot kapsamına almasını 6 adıma düşürmek.

## Cihaz hazırlık testi

`PilotReadinessProbe` Agent üzerinde çalışır ve hiçbir büyük yedek işlemi başlatmadan aşağıdaki kanıtları üretir:

| Kontrol | Davranış |
|---|---|
| Windows | Pilot Agent platformu doğrulanır. |
| Kaynak klasör | Klasör gerçekten erişilebilir mi? |
| NTFS / USN | NTFS ise hızlı artımlı takip uygun kabul edilir; değilse warning ve full-scan fallback. |
| VSS uygunluğu | VSS zorunlu politikada yerel fixed drive gerekir. |
| NAS yazma testi | Repository köküne 4 KiB random probe `WriteThrough` ile yazılır, flush edilir ve hemen silinir. |
| Boş alan | Varsayılan minimum 20 GiB; eşik API ile değiştirilebilir. |
| Repository key | Agent machine-DPAPI key ring içinde aktif AES key doğrulanır. |

Probe dosyası kullanıcı verisine dokunmaz; yalnız repository kökünde benzersiz `.yazmabackup-pilot-probe-<guid>.tmp` oluşturup kaldırır.

## Sistem hazırlık skoru

Control Plane şu başlıkları ağırlıklı skorlar:

- kayıtlı Agent,
- Agent online oranı,
- aktif backup policy,
- protection lock,
- kritik alarm,
- repository-health kanıtı,
- recovery run / restore kanıtı.

`pass = %100 ağırlık`, `warn = %50`, `fail = %0` olarak puanlanır.

## Politika profilleri

Profiller sabit kod sözleşmesidir; rastgele ayar üretmez. `PilotReadinessService.GetTemplates()` tek kaynak olarak UI ve API tarafından kullanılır.

## Toplu rollout atomikliği

`CreateBackupPoliciesAsync` bütün policy setini validate eder, bütün Agent kimliklerini doğrular ve yalnız sonra clone-state üzerinde politikaları ekleyip tek `CommitUnsafeAsync` çağrısı yapar. Bir Agent bulunamazsa hiçbir politika yazılmaz.

## Güvenlik sınırı

Pilot probe normal Operator yetkisiyle kuyruğa alınabilir; politika uygulama/rollout ise Backup Administrator yetkisi ister. Repository key provisioning ayrı Security Administrator yetkisinde kalır.


## Kullanıcı güvenlik bariyeri

Web sihirbazında hazır profil, aynı Agent + kaynak klasör + NAS yolu + repository kimliği için cihaz hazırlık testi çalıştırılmadan uygulanamaz. Hazırlık skoru **75/100 altındaysa** arayüz politikayı uygulamayı durdurur ve önce başarısız kontrollerin düzeltilmesini ister. Bu bariyer yeni/amatör kullanıcı akışını korur; yetkili otomasyonlar için API sözleşmesi ayrıca kullanılabilir.
