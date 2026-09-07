# R22.0 — Enterprise Recovery Fabric

## Büyük geçiş
R22, "backup var mı?" sorusundan "felaket anında ne kadar sürede geri dönebiliriz?" sorusuna geçer.

## Fleet Recovery Score
Etkin Recovery Plan'ların readiness puanlarının ortalamasıdır. Plan puanı:
- policy kapsamı %30,
- son recovery hedef başarı oranı %35,
- RTO hedefinin karşılanması %25,
- recovery kanıtının plan intervali içinde güncel olması %10.

Hiç gerçek recovery run yoksa sistem restore başarısı veya RTO uydurmaz; plan kanıtsız görünür.

## RTO
`RecoveryRunRecord.LongestRestoreMilliseconds` gerçek ölçümü, `RecoveryPlanRecord.RtoTargetMinutes` hedefiyle karşılaştırılır.
Tamamlanmış run + sıfır başarısız target + hedef sürede recovery şarttır.

## Kapsam
Recovery Plan içindeki PolicyId artık bulunmuyorsa planın readiness puanı düşer ve eksik policy sayısı görünür.

## DR görünümü
Yeni Recovery Fabric ekranı:
- Fleet Recovery Score
- hazır plan / etkin plan
- RTO ihlali
- kanıtsız plan
- recovery kapsamındaki policy sayısı
- plan bazında hedef/ölçülen RTO, target başarısı ve kapsamı gösterir.

## Güvenlik
R22 read-only recovery intelligence katmanıdır. Restore veya felaket kurtarma işlemini sessizce başlatmaz.

## Agent
Agent runtime/protokolü değişmedi.
