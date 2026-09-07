# YazmaBackup v1.2.0 — MeshCentral Fleet Connector

## Amaç

v1.2.0, mevcut MeshCentral altyapısını değiştirmeden `https://mesh.gursoyoto.com.tr` public adresi üzerinden cihaz envanteri, YazmaBackup Agent eşleştirme ve uzaktan Agent dağıtımını Control Plane içine alır. Reverse proxy arkasındaki MeshCentral backend portu (`4430`) YazmaBackup connector ayarı değildir.

## Yönetim ekranı

`MeshCentral` sayfasında connector adresi, entegrasyon kullanıcı adı, parola/login-key, `meshctrl.js` yolu ve senkronizasyon aralığı yönetilir. Credential ASP.NET Data Protection ile korunur ve kaydedildikten sonra UI'a geri gönderilmez.

## Bağlantı ve envanter

Connector `meshctrl.js` üzerinden `ServerInfo`, `ListDevices --json` ve uzaktan PowerShell komutları için `RunCommand` kullanır. UI içinde public adres HTTPS olarak tutulur; gerçek meshctrl çağrısında otomatik olarak `wss://` biçimine çevrilir. `ServerInfo` MeshCentral sürümünün native düz metin çıktısı olarak parse edilir; cihaz envanteri `ListDevices --json` ile alınır. Varsayılan otomatik senkronizasyon aralığı 5 dakikadır. `YAZMABACKUP_MESHCTRL_PATH` ile meshctrl yolu açıkça verilebilir. NVM for Windows kurulumlarında `NVM_SYMLINK` ve `NVM_HOME` da otomatik arama kapsamındadır.

Gerçek saha verisinde MeshCentral `name` alanı çoğu cihazda personel/görünen ad, `rname` ise gerçek Windows bilgisayar adı olarak geldiği için eşleştirme önceliği `hostname/rname -> Agent MachineName` şeklindedir. Bu eşleşme yoksa yalnız ikincil fallback olarak `name` denenir. Tek aday `matched`, aday yoksa `missing`, birden fazla aday varsa otomatik seçim yapılmadan `ambiguous` durumuna alınır.

## Tek tık Agent dağıtımı

Tek tık dağıtım için Control Plane'in endpoint cihazlardan erişilebilen HTTPS adresi `YAZMABACKUP_PUBLIC_BASE_URI` ile, yayımlanmış Agent paketi ise `YAZMABACKUP_AGENT_PACKAGE_ZIP` ile tanımlanır.

Akış:

1. Yönetici Agent eksik ve çevrimiçi cihazları seçer.
2. Control Plane kısa ömürlü, tek kullanımlık `ybboot_` bileti üretir.
3. MeshCentral `RunCommand` hedefte bootstrap URL'sini çağırır.
4. Bootstrap bileti tüketildiğinde tek kullanımlık enrollment grant ve `ybpkg_` package bileti üretilir.
5. Hedef Agent ZIP paketini HTTPS ile indirir.
6. ZIP SHA-256 doğrulaması başarısızsa kurulum durur.
7. `INSTALL_AGENT.ps1` Windows Service kurulumunu ve enrollment'ı gerçekleştirir.
8. Sonraki MeshCentral senkronizasyonunda Agent heartbeat/hostname kanıtı bulunursa deployment `succeeded` olur.
9. Cihaz çevrimiçi olduğu halde 30 dakika içinde Agent kanıtı oluşmazsa deployment `failed` olur.

## Güvenlik sınırları

MeshCentral admin hesabı yerine yalnız gerekli cihaz gruplarına yetkili ayrı bir servis hesabı kullanılmalıdır. Connector credential düz metin state'e yazılmaz. R3, gerçek meshctrl CLI sözleşmesindeki `--loginuser/--loginpass` modelini kullanır; parola işletim sistemi proses komut satırına eklenmez. `meshctrl-bridge.js` credential'ı yalnız child-process environment üzerinden alır, kendi environment'ından siler ve meshctrl process argv'sine process içinde ekler. Bootstrap/package biletleri tek kullanımlıdır ve kısa ömürlüdür. Public bootstrap endpointleri rate-limit altındadır. Agent paketi hash doğrulaması zorunludur.

## İlk gerçek saha doğrulaması

Aşağıdaki zincir Gürsoy ortamında çalıştırılmadan MeshCentral saha doğrulaması PASS sayılmaz:

`Bağlantıyı Test Et → Cihazları Senkronize Et → tek test PC seç → Agent Kur → heartbeat/version eşleşmesini gör`.


### R4 saha düzeltmeleri
- Gerçek MeshCentral RunCommand sözleşmesi `--run` olarak doğrulandı.
- Agent deployment API'si 504 üretmeyecek şekilde background queue + HTTP 202 mimarisine geçirildi.
- Restart recovery, 5 dakikalık subprocess timeout, monotonic deployment state ve success/error marker doğrulaması eklendi.
- İlk gerçek tek-cihaz Agent enrollment/heartbeat doğrulaması R4 saha kapısıdır.


### R5 installer reliability düzeltmeleri
- `INSTALL_AGENT.ps1` child PowerShell prosesi `-NoProfile -NonInteractive -ExecutionPolicy Bypass` ile çalışır; endpoint'in kalıcı execution policy ayarı değiştirilmez.
- Child installer exit code 0 değilse dağıtım doğrudan `failed` olur; `Remote installer completed` yanlış-pozitifi engellenir.
- Her dağıtım `C:\ProgramData\YazmaBackup\Logs\meshcentral-install-<deployment-id>.log` kalıcı kanıtı bırakır.
- Success marker için `YazmaBackupAgent` servisinin `Running` olması ve `agent.json` ile `agent-access-token.dpapi` enrollment evidence dosyalarının oluşması zorunludur.
- Servis başlangıcı beş saniyelik kararlılık penceresinden geçirilir; hata halinde SCM, Event Log ve Agent log özeti `service-health-*.log` dosyasına yazılır.
- `RUN_SERVER.ps1`, `YAZMABACKUP_AGENT_PACKAGE_ZIP` boş veya stale ise `dist` altındaki en güncel `YazmaBackupAgent_*_win-x64.zip` paketini otomatik seçer.
