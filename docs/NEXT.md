# Sonraki büyük geçiş — v1.2.0

v1.1.0 sonrasında küçük kozmetik sürüm hedeflenmez. Bir sonraki ana çalışma **Enterprise Production Gate & Fleet Autonomy** olacaktır.

Öncelikler:

1. Kullanıcı Windows ortamında `VERIFY.ps1` ile gelen gerçek compiler/runtime bulgularının kök neden düzeltmesi.
2. Gerçek SMB NAS fault-matrix sonuçlarının evidence olarak Control Plane'e alınması.
3. VSS writer sağlık sınıflandırması ve açık PST/Office restore kanıtı.
4. USN wrap/recreate/fallback doğrulamasının otomatik pilot runbook'a bağlanması.
5. Agent update health-check + rollback run'larının imzalı evidence paketine girmesi.
6. Fleet-level self-healing policy: yalnız allowlist aksiyonlar, maintenance window, canary, max-concurrency ve otomatik rollback.
7. Repository health forecast + otomatik kapasite aksiyon önerileri.
8. Güncel PostgreSQL provider sürümü dış kaynaktan doğrulanabildiğinde normalized runtime cutover + gerçek multi-node HA.
9. İmzalı pilot kabul raporu ve production gate checklist.
