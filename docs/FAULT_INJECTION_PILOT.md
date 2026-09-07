# Fault Injection Pilot

`PILOT_FAULT_INJECTION.ps1` yalnız izole laboratuvar için tasarlanmıştır.

Zorunlu güvenlik kapıları:
- `-Execute` olmadan çalışmaz.
- Repository ID `LAB-` ile başlamalıdır.
- RepositoryRoot içinde `YazmaBackup-FaultLab` bulunmalıdır.
- Test mevcut olmayan bir alt klasörü hedefleyerek üç kontrollü repository-health arızası üretir.
- Silme, rename, encryption veya mevcut repository içeriğine yazma işlemi yapmaz.

Beklenen sonuç: Agent üç transient `DirectoryNotFound/IOException` sonrası circuit'i açar; heartbeat'te `RepositoryCircuits` görünür ve Control Plane alarm üretir. Sonraki gerçek başarılı repository erişimi circuit'i kapatır.
