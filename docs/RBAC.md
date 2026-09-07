# RBAC ve Yönetim Kimlikleri — v1.1.0

Roller: `viewer`, `operator`, `backup-admin`, `security-admin`, `administrator`.

- Viewer: okuma ve görünürlük.
- Operator: browse, backup, restore, alarm acknowledgement.
- Backup Administrator: policy, scrub, restore-drill yönetimi.
- Security Administrator: enrollment, repository key, Agent update, kullanıcı görünürlüğü/yönetimi ve audit.
- Administrator: tüm yetkiler, Administrator hesapları, API token issuance ve break-glass.

Security Administrator yeni `administrator` hesabı oluşturamaz veya mevcut Administrator hesabını etkin/pasif değiştiremez. Administrator kapsamlı Management API Token ve break-glass kodu yalnız **interaktif cookie Administrator oturumundan** üretilebilir. API tokenın kendisi yeni Administrator token üretemez.

Legacy `X-YazmaBackup-Admin-Key` varsayılan kapalıdır. Yalnız açıkça `YAZMABACKUP_ENABLE_LEGACY_ADMIN_KEY=true` verilirse geçiş/lab amacıyla açılır.
