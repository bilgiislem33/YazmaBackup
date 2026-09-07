# MeshCentral Status Ingestion

v1.1.0 MeshCentral'ın doğrulanmamış dış API endpoint'lerini tahmin etmez. Önce Agent↔opaque Node ID eşlemesi yapılır; sonra MeshCentral automation/script dar kapsamlı status-ingestion API'sine veri yollar.

Entegrasyon credential prefix'i `ybit_`'dir. Secret yalnız oluşturulduğu anda döndürülür; Control Plane SHA-256 hash saklar. İlk desteklenen purpose `meshcentral-status` ile sınırlandırılmıştır. Token 1–365 gün geçerlidir ve revoke edilebilir.

Status report; event ID, Agent ID, Node ID, node status, deployment status ve reported timestamp taşır. Node ID kayıtlı link ile birebir eşleşmelidir. Event ID idempotenttir; aynı ID farklı kimlikle yeniden kullanılamaz. Replay penceresi gelecekte +5 dakika, geçmişte 7 gün ile sınırlıdır.
