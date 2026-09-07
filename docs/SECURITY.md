# YazmaBackup v1.1.0 Güvenlik Temeli

## Trust boundaries

- Agent endpoint işletim sistemi ve LocalSystem/servis sınırı.
- Control Plane yönetim API'si.
- NAS/repository veri alanı.
- MeshCentral yalnız dağıtım/operasyon kanalıdır.

## Agent secret'ları

Agent identity/access token, repository key ring ve RSA private key Windows machine-scope DPAPI ile korunur; `%ProgramData%\YazmaBackup` altında ACL sertleştirmesi uygulanır. Bootstrap enrollment secret başarılı kayıt sonrasında silinir.

## Enrollment

Kalıcı ortak enrollment parolası yerine süreli ve `RemainingUses` limitli token vardır. Token Control Plane state'inde yalnız SHA-256 hash olarak tutulur. Agent access tokenı da yalnız hash olarak saklanır.

## Repository encryption

- AES-256-GCM.
- 96-bit random nonce.
- 128-bit authentication tag.
- AAD domain separation.
- Key ID authenticated envelope headerıyla içerik ve anahtar seçimi bağlanır.
- Chunk plaintext SHA-256 restore/scrub sırasında ayrıca doğrulanır.
- Tam dosya SHA-256 manifestte bulunur ve restore/scrub sonunda yeniden hesaplanır.

## Repository metadata anti-downgrade

Encrypted repository metadata düz JSON değildir; `repository.meta` AES-GCM authenticated envelope'dur. Metadata kaybolmuşken encrypted/v3 veri görülürse repository otomatik yeniden başlatılmaz. Encrypted repository'de plaintext objeler normal backup/restore sırasında reddedilir; yalnız açıkça başlatılan legacy migration bunları okuyabilir.

## Remote repository key provisioning

Agent RSA-3072 key pair üretir. Private key DPAPI ile Agent'ta kalır, public key heartbeat ile Control Plane'e gider. Yönetici repository key'i TLS üzerinden gönderir; Control Plane key'i Agent public key'iyle RSA-OAEP-SHA256 sarar ve command state'e yalnız wrapped ciphertext koyar. Agent unwrap ettikten sonra key'i DPAPI key ring'e alır ve clear byte buffer'larını sıfırlar.

## Key rotation / retirement

Yeni key active yapıldıktan sonra scrub/rekey bütün chunk ve manifestleri yeni key'e geçirir. Eski key silme isteği Agent tarafında repository header taramasına tabidir. Herhangi bir object eski key ID'sine referans veriyorsa removal fail-closed olur.

## Command güvenliği

- Agent bearer token.
- 2 dakikalık command lease.
- lease renewal.
- max 5 delivery attempt.
- idempotency key.
- Agent tarafında completed-result journal.
- expired/lost lease ile result commit reddi.

## VSS ve okunamayan veri

Varsayılan `RequireSnapshot=true`. VSS başarısızsa live-read fallback yalnız açıkça yapılandırılmışsa devreye girer. Backup enumerasyon/okuma hataları sessizce yutulmaz; eksik backup başarılı raporlanmaz.

## Update supply chain

Update metadata ECDSA P-256/SHA-256 ile doğrulanır; package SHA-256 ayrı doğrulanır ve version downgrade reddedilir. v1.1.0 doğrulanmış staging sağlar. Atomic apply + post-update health-check + otomatik rollback tam üretim kapısı sonraki sürüme bırakılmıştır; mevcut sürüm bunu varmış gibi raporlamaz.

## Management plane güvenliği

v1.1.0 local management users, PBKDF2-HMAC-SHA256 parola hash'i, 5-deneme hesap lockout'u, HttpOnly/SameSite cookie, CSRF, login rate-limit, endpoint seviyesinde RBAC ve audit trail ekler. Legacy `X-YazmaBackup-Admin-Key` varsayılan olarak kapalıdır; yalnız açıkça `YAZMABACKUP_ENABLE_LEGACY_ADMIN_KEY=true` verilirse geçiş/lab amacıyla açılır. CLI varsayılanı süreli ve hashlenmiş Management API Token'dır. İlk bootstrap administrator `MustChangePassword=true` ile oluşturulur.

Data Protection key ring Control Plane state alanında kalıcıdır. Üretimde HTTPS zorunludur; `YAZMABACKUP_ALLOW_INSECURE_UI_COOKIE=true` yalnız kapalı lab için kullanılabilir.

## Bilinen üretim kapıları

v1.1.0 henüz GA değildir. OIDC/SSO, ransomware preflight, append-only manifest ve yazılımsal immutability mevcut olsa da gerçek üretim kapıları; normalized PostgreSQL runtime store + multi-node HA, KMS/HSM/vault entegrasyonu, NAS tarafında doğrulanmış gerçek WORM/Object Lock, repository-wide distributed GC lease, imzalı installer/supply-chain pipeline ve Windows/NAS fault-matrix pilotudur. PostgreSQL şema sözleşmesi hazırdır ancak doğrulanmış güncel driver sürümü bu build ortamında teyit edilemediği için runtime adapter bilinçli olarak etkin değildir.


## v1.1.0 yönetim kimliği sertleştirmesi

OIDC Authorization Code + PKCE, RS256/JWKS doğrulaması, issuer/audience/azp/nonce/lifetime kontrolleri; süreli rol kapsamlı API tokenları; tek kullanımlık break-glass kodları ve issuer+subject dış kimlik bağı eklendi. OIDC mapping yerel administrator rolünü veremez.
