# PostgreSQL — v1.1.0

Migration 001–004; Agent/command/policy/user/audit, protection, restore-drill, Management API Token, break-glass, OIDC identity, alarm, cluster lease ve state staging şemalarını içerir.

`INIT_POSTGRESQL.ps1` SHA-256 checksum drift koruması uygular. Aynı migration version farklı içerikle gelirse işlem durur.

Bu çalışma ortamında web erişimi kapalı olduğundan Ağustos 2026 için desteklenen güncel Npgsql/provider sürümü doğrulanamamıştır. **Doğrulanmamış** package version projeye eklenmemiştir. Bu nedenle normalized PostgreSQL runtime store bu sürümde aktif değildir ve `productionReady=false` kalır.

Bkz. `POSTGRESQL_CUTOVER.md`.
