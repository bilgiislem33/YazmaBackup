# Production Fabric — v1.1.0

Production Fabric, endpoint backup motorunun arıza altında kontrollü davranmasını ve DR sonucunun kriptografik kanıt üretmesini sağlayan katmandır.

Akış: `Agent → repository circuit breaker → VSS/USN/CDC/AES-GCM → NAS → restore drill → Recovery Plan → Recovery Runbook → signed evidence`.

Control Plane'deki `production-fabric-orchestrator` ayrı fenced lease kullanır. Aynı runbook için eşzamanlı ikinci run başlatılmaz; schedule ilerlemesi state commit'iyle atomiktir. Runbook, Recovery Plan'ları sıralı yürütür. Bir adım başarısızsa veya toplam RTO bütçesi aşılırsa bütün run başarısız olur ve alarm üretilir.

Kaynak model state schema 9'dur. PostgreSQL normalize karşılığı migration 006 içindedir; runtime provider doğrulanmadığı için Source RC'de JSON state tek-node olarak kalır.
