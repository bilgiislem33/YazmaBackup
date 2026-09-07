# R27.3.1 — Dependency + VERIFY Root-Cause Fix

Windows sahasında iki bağımsız bootstrap engeli görüldü.

1. `@tremor/react 3.18.7` React 18 peer dependency isterken frontend React 19.1.1 kullanıyordu. `--force` veya `--legacy-peer-deps` ile bastırılmadı. Tremor yalnız dashboarddaki iki kartta kullanıldığı için bağımlılık tamamen kaldırıldı ve aynı görünüm mevcut native YazmaBackup Card/Progress bileşenleriyle oluşturuldu.
2. Ana VERIFY tüm PackageReference'ları reddediyordu; ancak R12'den beri PostgreSQL state engine için `Npgsql 10.0.0` ürünün bilinçli ve gerekli bağımlılığıdır. Gate kaldırılmadı: tam tersine yalnız `Npgsql=10.0.0` izinli olacak şekilde explicit allowlist'e çevrildi. Başka paket/sürüm yine fail-closed reddedilir.

Agent ve runtime deployment binding değişmedi.
