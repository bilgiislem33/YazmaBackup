# R23.0 — Business Continuity Command Center

## Büyük geçiş
R22 teknik DR hazırlığını ölçüyordu. R23 bu veriyi iş sürekliliği görünümüne taşır: felaket anında hangi iş servisinin önce ele alınacağını ve hangi servisin recovery kapsamının eksik olduğunu gösterir.

## İş servisi modeli
Mevcut veri modelinde resmi Department/BusinessService alanı olmadığı için R23 kurumsal sahiplik uydurmaz.
İş servisi grubu policy adındaki açık önekten türetilir (`Muhasebe - ...`, `Servis / ...`, vb.). Ayrıştırılabilir önek yoksa policy adı kendi servis adı olarak kalır.

## Kritik seviye
Kritiklik gerçek operasyonel sinyallerden türetilir:
- RTO <= 30 dakika: critical
- RTO <= 120 dakika veya 3+ policy: high
- diğerleri: standard

## Continuity Score
İş servisinin puanı R22 Recovery Fabric plan puanlarından gelir; Recovery Plan dışında kalan policy varsa ayrıca düşürülür.

## Disaster Simulation
Kurtarma sırası criticality -> RTO -> isim sırasına göre deterministik oluşturulur.
Bu bir yürütme planıdır; restore işlemi otomatik başlatılmaz.

## Güvenlik
R23 read-only komuta merkezi katmanıdır. Gerçek restore/recovery operasyonları mevcut yetki ve onay kapılarında kalır.

## Agent
Agent runtime/protokolü değişmedi.
