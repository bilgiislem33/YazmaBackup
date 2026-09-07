# OIDC / Entra ID SSO — v1.1.0

YazmaBackup v1.1.0, harici framework bağımlılığı eklemeden OpenID Connect Authorization Code + PKCE akışını yönetim düzlemine ekler.

## Güvenlik kapıları

- `state`, `nonce` ve PKCE `S256` zorunlu.
- Login transaction ASP.NET Core Data Protection ile korunmuş, HttpOnly `SameSite=Lax` cookie içinde en fazla 10 dakika tutulur.
- ID token yalnız `RS256` kabul eder.
- JWKS anahtarı `kid`, `kty=RSA`, `use=sig` ve `alg=RS256` koşullarıyla seçilir; RSA anahtarı en az 2048 bittir.
- `iss`, `aud`, `exp`, `nbf`, `iat`, `nonce` doğrulanır. Birden fazla audience varsa `azp=client_id` zorunludur.
- OIDC role mapping hiçbir koşulda yerel `administrator` rolü veremez. Break-glass Administrator yerel kalır.
- OIDC identity bağı `issuer + subject` üzerinden yapılır; username claim'i mevcut yerel hesabı otomatik sahiplenemez.

## Ortam değişkenleri

```powershell
$env:YAZMABACKUP_OIDC_ISSUER = "https://login.microsoftonline.com/TENANT-ID/v2.0"
$env:YAZMABACKUP_OIDC_CLIENT_ID = "CLIENT-ID"
$env:YAZMABACKUP_OIDC_REDIRECT_URI = "https://backup.example.com/api/v1/session/oidc/callback"
$env:YAZMABACKUP_OIDC_DEFAULT_ROLE = "viewer"
$env:YAZMABACKUP_OIDC_ROLE_MAPPINGS = "Backup-Operators=operator;Backup-Admins=backup-admin;Backup-Security=security-admin"
```

Confidential client kullanılıyorsa `YAZMABACKUP_OIDC_CLIENT_SECRET` yalnız güvenli secret store üzerinden verilmelidir. OIDC endpointleri varsayılan olarak HTTPS olmak zorundadır. `YAZMABACKUP_OIDC_ALLOW_INSECURE_ENDPOINTS=true` yalnız izole laboratuvar içindir.
