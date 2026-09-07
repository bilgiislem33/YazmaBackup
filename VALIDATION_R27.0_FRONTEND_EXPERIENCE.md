# R27.0 — Frontend Experience / Command Center

## Amaç
YazmaBackup'ı klasik yönetim panelinden canlı Backup Operations Center deneyimine taşımak.

## Gerçek veri prensibi
R27 demo/sahte telemetri üretmez. Command Center mevcut backend uçlarını kullanır:
- `/api/v1/admin/commands?limit=100`
- `/api/v1/admin/repository-health?limit=1000`
- `/api/v1/admin/alarms/summary`
- mevcut dashboard / operational-health / agents / policies

Backend gerçek yüzde/byte progress sağlamıyorsa UI yüzde uydurmaz. Aktif command yalnız hareketli activity signal olarak gösterilir ve kullanıcıya bunun yüzde olmadığı açıkça yazılır.

## Yeni deneyim
- Command Center
- 5 saniyelik live command polling
- Fleet Online
- Protection Score
- Active Operations
- Critical Risk
- Repository Risk
- Policy coverage
- Repository Radar
- Live Activity Stream
- Fullscreen NOC Mode
- mevcut Ctrl+K command palette korunur

## R26 doğruluk düzeltmesi korunuyor
- legacy-ui yok
- source wwwroot yok
- tek UI kaynağı `YazmaBackup.Frontend`
- R26 MSBuild production publish contract korunuyor
- yanıltıcı `R10.0 Full React Cutover` dashboard banner kaldırıldı

## Bilinçli sınırlar
R27 henüz gerçek byte-level backup progress, throughput veya ETA göstermez; backend bu telemetriyi güvenilir şekilde sunmadan bunlar uydurulmayacaktır.
Fleet Galaxy ve Recovery Timeline sonraki büyük frontend aşamalarıdır.

## Agent
Agent runtime değişmedi.
