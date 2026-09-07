# Enterprise Resilience Plane

v1.1.0 yedek sisteminin kendisini sürekli denetleyen katmandır.

## Repository sağlık taraması

Her politika varsayılan olarak 24 saatte bir `RepositoryHealthScan` üretir. Agent repository kapasitesini, boş alanı, fiziksel repository büyüklüğünü, restore-point sayısını ve son yedi günlük yeni veri miktarını ölçer. Tahmini doluluk 14 günün altına inerse uyarı, 3 günün altına inerse kritik durum oluşur. Son restore point yoksa kritik kabul edilir.

## Recovery Plan

Bir Recovery Plan bir veya daha fazla backup policy içerir. `MaxParallelAgents` aynı anda kaç drill komutunun aktif olabileceğini, `RtoTargetMinutes` hedef RTO'yu belirler. Orchestrator RestoreDrill komutlarını gerçek Agent'lara dağıtır, sonuçları toplar ve doğrulanan byte ile en uzun restore süresini run kanıtına yazar. Hata veya RTO aşımı planı başarısız yapar ve Alarm Merkezi'nde kritik olay üretir.

## Liderlik

Resilience Orchestrator kendi `resilience-orchestrator` lease'ini kullanır. JSON state backend'i tek-node RC içindir. Migration 005, PostgreSQL runtime adapter devreye alındığında lease acquisition'ın tek SQL statement ve monotonic fencing epoch ile yapılması için atomik fonksiyon sözleşmesini içerir. Runtime adapter doğrulanmadan gerçek multi-node HA iddiası yapılmaz.
