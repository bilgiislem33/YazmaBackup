# PostgreSQL Cutover Hazırlığı — v1.1.0

v1.1.0 yedi sıralı migration taşır (001–007). `INIT_POSTGRESQL.ps1` migration SHA-256 değerini `schema_migrations` tablosuna yazar. Aynı version daha sonra farklı checksum ile gelirse **MIGRATION DRIFT** hatasıyla durur.

Önerilen akış:

```powershell
.\scripts\PREFLIGHT_POSTGRESQL.ps1 -Server "PG01" -Database "yazmabackup" -AdminUser "yb_admin"
.\scripts\INIT_POSTGRESQL.ps1 -Server "PG01" -Database "yazmabackup" -AdminUser "yb_admin"
.\scripts\STAGE_JSON_STATE_POSTGRESQL.ps1 -StateFile ".\.local\control-plane-state.json" -Server "PG01" -Database "yazmabackup" -AdminUser "yb_admin"
```

`STAGE_JSON_STATE_POSTGRESQL.ps1` schema 10 state dosyasını SHA-256 ile mühürleyip `control_plane_state_snapshots` tablosuna `staged` olarak yükler. **Bu bir runtime cutover değildir.** JSON state dosyası silinmez ve active backend değiştirilmez.

Bu oturumda web doğrulaması kapalı olduğundan Ağustos 2026 için desteklenen Npgsql sürümü doğrulanamamıştır. Rastgele bir package version eklemek yerine normalized PostgreSQL adapter bilinçli olarak kapalı bırakılmıştır.
