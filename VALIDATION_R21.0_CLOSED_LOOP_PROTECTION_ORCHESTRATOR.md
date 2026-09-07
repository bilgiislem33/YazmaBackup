# R21.0 — Closed-Loop Protection Orchestrator

## Büyük geçiş
R20 yaklaşan riski görüyordu. R21 bu riski mevcut Autonomous Remediation motoruna bağlayarak bir yaşam döngüsü oluşturur:

TESPİT → TEŞHİS → ONAY (gerekiyorsa) → UYGULAMA → DOĞRULAMA → DOĞRULANDI

## Güvenli teşhis
SLA ve restore-evidence vakalarında ilgili gerçek Backup Policy üzerinden SourcePath, RepositoryRoot ve RepositoryId çözülür. Kullanıcıdan serbest path alınmaz. `diagnose-only` allowlist aksiyonu kullanılır.

Aynı Agent/repository için aktif `queued-diagnosis` veya `diagnosing` run varsa ikinci duplicate run oluşturulmaz.

## Riskli değişiklikler
R21 güvenlik sınırını kaldırmaz. Repository circuit reset veya cleanup gibi mutasyonlar mevcut security-admin approval zincirinde kalır.

## İşlem sonrası doğrulama
Mevcut Autonomous Remediation state machine `applying` sonrasında `SelfHealingDiagnose` verify komutu üretir ve sonucu `verifying → completed/failed` olarak kaydeder. R21 bu durumu Closed-Loop vaka ekranında gösterir.

## Önemli sınır
R21 her tahmini otomatik düzeltmeye çalışmaz. Kapasite artışı gibi fiziksel/operasyonel işler planlama ve onay gerektirir. Bu bilinçli güvenlik tasarımıdır.

## Agent
Agent runtime/protokolü değişmedi.
