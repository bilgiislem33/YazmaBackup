# YazmaBackup v1.2.0 R4 - MeshCentral Deployment Reliability

## R4 amacı
R3 saha testinde doğrulanan MeshCentral davranışlarını kalıcı hale getirir ve Agent Kur isteğinin ters proxy altında 504 ile beklemesini kaldırır.

## Saha tarafından doğrulanmış girdiler
- `ServerInfo` gerçek MeshCentral ortamında PASS.
- `ListDevices --json` gerçek MeshCentral ortamında PASS.
- Gerçek `RunCommand` sözleşmesi `--run` parametresidir; `--cmd` hatalıdır.
- Gerçek hedef node üzerinde `RUNCOMMAND-PASS` dosya/çıktı kanıtı alındı.
- Hedef cihazdan `https://backup.gursoyoto.com.tr` HTTPS erişimi PASS.
- Windows R3 kaynak ağacı .NET 10 Release / warnings-as-errors kalite kapısını geçti.

## R4 kapatılan hatalar
1. `RunCommand --cmd` kalıntısı kaldırıldı ve VERIFY ile geri dönmesi engellendi.
2. `/meshcentral/deploy` MeshCentral prosesini HTTP request içinde beklemiyor; 202 Accepted + background worker kullanıyor.
3. Worker `queued` işleri restart sonrası yeniden keşfediyor.
4. Remote installer success/error marker üretip worker tarafından doğrulanıyor.
5. meshctrl exit code 0 olsa bile bilinen CLI hata çıktıları başarısız sayılıyor.
6. 5 dakikalık deployment command timeout var.
7. Bootstrap sırasında `installing` olmuş kaydın sonradan `dispatched` durumuna geri düşmesi engellendi.
8. Pending sayacı `queued/dispatching/dispatched/installing` durumlarını kapsıyor.
9. RUN_SERVER state yolu gerçek Control Plane content-root `.local` yolu ile hizalandı.
10. VERIFY teknik borç taraması generated `bin/obj/dist/artifacts/publish` klasörlerini dışlıyor.
11. Windows Data Protection key ring DPAPI ile korunuyor.

## R4 saha kabul kapısı
Aşağıdaki kanıtlar alınmadan `productionReady=true` yapılmaz:
- Tek hedefte `queued -> dispatching -> installing -> succeeded`.
- Hedefte `YazmaBackupAgent` servisi Running.
- Control Plane'de aynı cihaz için yeni Agent enrollment + heartbeat.
- Gerçek NAS backup + restore doğrulaması.
- PostgreSQL runtime/cutover doğrulaması.

## Windows doğrulama
```powershell
cd C:\YazmaBackup
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\VERIFY.ps1
```

R4 paketi Linux build ortamında .NET SDK bulunmadığı için burada C# derlemesi yapılmadan mühürlenir; Windows üzerindeki 23/23 VERIFY sonucu saha kabul kanıtıdır.
