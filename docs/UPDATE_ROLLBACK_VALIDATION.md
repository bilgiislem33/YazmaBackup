# Agent Update + Rollback Validation — v1.1.0

`INSTALL_AGENT.ps1` v1.1.0 ile yalnız Windows servisinin `Running` durumuna gelmesini yeterli saymaz.

Opsiyonel olarak `-ManagementAccessToken` ve `-ExpectedAgentId` verildiğinde:

1. yeni Agent dosyaları versioned `Agent-1.1.0` klasörüne kopyalanır,
2. bootstrap tamamlanır,
3. Windows servisi yeni ImagePath ile başlatılır,
4. Control Plane `/api/v1/admin/agents` üzerinden aynı Agent ID'nin yeni sürümle heartbeat vermesi beklenir,
5. heartbeat/version doğrulanmadan eski Agent klasörleri silinmez,
6. health-check süre aşımına uğrarsa servis eski `ImagePath` ile geri alınır ve eski servis tekrar başlatılır.

Bu mekanizma rollback davranışını güçlendirir; yine de gerçek update/rollback pilotu Windows üzerinde kontrollü bir test cihazında çalıştırılmadan üretim doğrulama yüzdesi artırılmaz.
