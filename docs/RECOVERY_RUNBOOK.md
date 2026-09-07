# Recovery Runbook ve Recovery Evidence

Recovery Plan tek bir DR hedef grubunu gerçek restore-drill komutlarıyla test eder. Recovery Runbook bir veya daha fazla Recovery Plan'ı sıralı orkestre eder.

Her runbook run; başlangıç/bitiş, aktif adım, adım→RecoveryRun bağı, hata ve toplam RTO bütçesini saklar. RTO bütçesi dolarsa kalan adımlar fail-closed kapatılır.

`/api/v1/admin/recovery-runbook-runs/{runId}/evidence` endpoint'i runbook run, ilgili recovery run'lar, son repository-health kayıtları ve MeshCentral sync kanıtını JSON payload'a dönüştürür. Payload SHA-256 hashlenir ve ECDSA P-256/SHA-256 ile imzalanır. Private key ASP.NET Data Protection ile korunur ve düz metin state'e yazılmaz.

Evidence bir bağımsız doğrulama artefaktıdır; gerçek NAS/Windows pilot testi yerine geçmez.
