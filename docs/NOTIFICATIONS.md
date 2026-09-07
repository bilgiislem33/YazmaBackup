# Alarm Bildirim Yolları

v1.1.0 ilk bildirim sağlayıcısı olarak HTTPS webhook kullanır.

- Hedef yalnız HTTPS olabilir.
- Host `YAZMABACKUP_NOTIFICATION_ALLOWED_HOSTS` içinde açıkça bulunmalıdır.
- HMAC secret düz metin state'e yazılmaz; ASP.NET Data Protection ile korunur.
- Her teslim `X-YazmaBackup-Delivery`, `X-YazmaBackup-Timestamp` ve `X-YazmaBackup-Signature: sha256=...` taşır.
- İmza girdisi `timestamp + "." + body` biçimindedir.
- Aynı route/alarm/LastSeenAtUtc sürümü bir kez hazırlanır.
- Teslim üç kontrollü denemeden sonra başarılı veya failed olarak kapanır.
- Response body loglanmaz; secret hiçbir audit kaydına yazılmaz.

Örnek allowlist:

```powershell
$env:YAZMABACKUP_NOTIFICATION_ALLOWED_HOSTS = "alerts.gursoyoto.com.tr"
```
