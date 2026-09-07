# YazmaBackup v1.2.0 R5.1 - Windows Service Creation Reliability

## Saha kök nedeni ve kalıcı düzeltme

R5 gerçek MeshCentral dağıtımında Agent bootstrap aşamasını geçti, ancak Windows servis oluşturma adımı başarısız oldu. R5 servis oluşturmayı `sc.exe create ... start= delayed-auto` komutuna bağlıyordu ve stdout `Out-Null` ile kaybedildiği için yalnız genel `Windows servisi oluşturulamadı` mesajı görüldü.

R5.1 aşağıdaki kalıcı değişiklikleri uygular:

- Varsayılan LocalSystem servisini `New-Service` ile oluşturur; `sc.exe create` argüman ayrıştırmasına bağımlılığı kaldırır.
- SCM geçici/marked-for-delete yarışları için servis oluşturmayı en fazla 5 kez, 2 saniye arayla dener.
- Automatic Delayed Start davranışını `Start=Automatic` + `DelayedAutoStart=1` ile açıkça uygular.
- gMSA ve mevcut servis config işlemleri için `Invoke-ScStrict` kullanır; `sc.exe` exit code ve tam çıktısını hata metnine taşır.
- Servis oluşturulduktan sonra SCM kaydı ve `ImagePath` doğrulanmadan devam etmez.
- PowerShell stdout/stderr UTF-8 olarak ayarlanır; MeshCentral hata mesajlarındaki Türkçe karakter bozulması azaltılır.
- R5'in ExecutionPolicy Bypass, child exit-code propagation, kalıcı diagnostics, service-running ve enrollment-evidence doğrulamaları aynen korunur.
- VERIFY yeni R5.1 invariantlarını kontrol eder.

## Kabul kapısı

Windows üzerinde `scripts\VERIFY.ps1` gerçek .NET 10 derleme/test kapıları PASS olmadan ve en az bir gerçek MeshCentral endpoint dağıtımı `queued -> dispatching -> installing -> succeeded` zincirini tamamlamadan `productionReady` false kalır.
