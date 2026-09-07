# R24.0 — Business Service Graph + Dependency-Aware Recovery DAG

## Büyük sıçrama
R23 kritik iş servislerini sıralıyordu. R24 servisler arasındaki gerçek bağımlılıkları kalıcı veri olarak tutar ve recovery sırasını topological DAG ile hesaplar.

Örnek: ERP, SQL'e bağımlıysa SQL her zaman ERP'den önce recovery sırasına girer.

## Kalıcı dependency modeli
`BusinessServiceDependencyRecord`: ServiceId, DependsOnServiceId, DependencyType (`hard|soft`), Required.
StateStore içinde korunur; file/PostgreSQL authoritative state CAS mekanizmasını kullanır. Serbest geçici UI state değildir.

## Fail-closed cycle koruması
A → B → A gibi zorunlu dependency cycle oluşursa servisler güvenli sıraya zorla sokulmaz. `BLOCKED` görünür ve graph düzeltilmelidir.

## Yetki
Dependency oluşturma/silme `backupAdmin` write gate altındadır. Graph okuma `readAdmin` ile yapılır.

## Recovery yürütme
R24 DAG hesaplar fakat restore/recovery'yi sessizce başlatmaz. Mevcut recovery/onay güvenlik sınırları korunur.

## Agent
Agent runtime/protokolü değişmedi.
