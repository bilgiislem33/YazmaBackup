# Repository Circuit Breaker

Agent repository operasyonları için transient hata sınıfları `IOException`, `UnauthorizedAccessException` ve `TimeoutException` olarak sınırlandırılmıştır. Üçüncü ardışık transient hatada devre açılır.

Backoff: 3. hata 1 dk, 4. hata 5 dk, 5. hata 15 dk, 6. hata 30 dk, 7+ hata 60 dk. Başarılı repository erişimi sayacı ve devreyi sıfırlar.

State `%ProgramData%\YazmaBackup\repository-circuits.json` altında atomik/write-through yazılır. Agent restart sonrası devre bilgisi korunur. Corrupt circuit state sessizce sıfırlanmaz; fail-closed `InvalidDataException` üretir.

Circuit telemetrisi heartbeat ile Control Plane'e aktarılır. Açık devre Alarm Merkezi'nde görünür; 6+ ardışık hata kritik seviyeye yükselir.
