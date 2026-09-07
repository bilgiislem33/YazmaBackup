# YazmaBackup v1.2.0 R5.8 — Heartbeat-Driven Deployment Reconciliation

## Kök neden
R5.7 saha testinde uzak installer, Windows servisi ve local enrollment kanıtı başarıyla tamamlandı; ancak deployment `installing` durumunda kaldı. Deployment başarısı yalnız MeshCentral inventory sync sırasında Agent eşleşmesi bulunursa kesinleştiriliyordu. Agent heartbeat sync çağrısından birkaç saniye sonra gelirse durum normal 1-5 dakikalık inventory periyoduna kadar bekleyebiliyordu.

## R5.8 kalıcı düzeltme
- `/api/v1/agent/heartbeat` başarılı `TouchAsync` sonrasında pending MeshCentral deployment'ları event-driven olarak reconcile eder.
- Host eşleşmesi yalnız unique MeshCentral `Hostname/Name` adayı varsa deployment'ı `succeeded` yapar; duplicate/ambiguous host isimlerinde otomatik bağlama yapılmaz.
- Başarı kaydına gerçek `AgentId`, machine name ve agent version yazılır.
- Deployment worker, herhangi bir kayıt `installing` durumundayken normal sync intervalini beklemek yerine recovery çevriminde yaklaşık 10 saniyede bir MeshCentral sync fallback çalıştırır.
- Control Plane restart sonrası `installing` kayıtlar da fallback reconciliation ile tekrar değerlendirilebilir.
- R5.7 service configuration ve R5.6 ACL self-heal değişmeden korunur.

## Saha kabulü
Agent Kur sonrası installer/local enrollment PASS görüldüğünde ilk başarılı Agent heartbeat ile deployment birkaç saniye içinde `succeeded` olmalıdır. Aynı hostname'e sahip birden fazla MeshCentral node varsa güvenli biçimde `installing` kalmalı ve otomatik yanlış node-agent bağlaması yapılmamalıdır.
