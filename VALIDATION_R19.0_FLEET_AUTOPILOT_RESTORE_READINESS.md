# R19.0 — Fleet Autopilot + Restore Readiness

## Kullanıcı açısından büyük değişiklik
R18 sorunları buluyordu. R19 bu sorunları uygulanabilir bir çözüm planına dönüştürüyor.

Yeni Fleet Autopilot ekranı:
- filonun gerçekten geri yüklemeye hazır olup olmadığını puanlar,
- son 30 günlük recovery/restore testlerini değerlendirir,
- SLA riski taşıyan backup/gecikme/kapasite problemlerini toplar,
- güvenli otomatik teşhis yapılabilecek işleri ayırır,
- değişiklik gerektiren işleri yönetici onayına bırakır.

## Restore Hazırlık Puanı
Sadece "backup başarılı" olması yeterli kabul edilmez. Son recovery testlerinin başarısı ve test kanıtlarının güncelliği birlikte değerlendirilir. Hiç restore testi yoksa sistem yüksek puan vermez.

## Autopilot güvenlik sınırı
R19 kendi kendine eski backup silmez, protection lock kaldırmaz veya riskli repository değişikliği yapmaz.
Planlar üç sınıfa ayrılır:
- auto-diagnose: güvenli teşhis adayı,
- approval-required: yönetici onayı gerekir,
- operator-plan: insan planlaması gerekir.

Mevcut R10.4 Autonomous Remediation onay mekanizması korunur.

## Agent
Agent runtime/protokolü değişmedi.
