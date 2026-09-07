# R18.0 — Enterprise Backup Intelligence

## Kullanıcı açısından ne değişti?
Yönetici artık yüzlerce bilgisayarın yedekleme durumunu tek tek kontrol etmek zorunda değil.

Yeni Backup Intelligence ekranı sistemdeki verileri birlikte değerlendirir:
- hangi bilgisayar çevrimdışı,
- hangi yedekleme planı gecikmiş,
- son 24 saatte hangi yedeklemeler başarısız,
- hangi bilgisayarda koruma kilidi var,
- hangi NAS/repository dolmaya yaklaşıyor.

Sonuçları tek bir Filo Sağlık Puanında toplar ve en önemli beş problemi önce gösterir.

## Güvenlik yaklaşımı
R18 analiz yapar ve öneri üretir; riskli değişiklikleri sessizce uygulamaz.
Mevcut Autonomous Remediation sistemi korunur ve mutasyon gerektiren işlemler yönetici onayına bağlı kalır.

## NAS kapasite tahmini
Repository health ölçümlerindeki büyüme ve EstimatedDaysToFull bilgisi kullanılır.
14 gün veya daha az kalan repository kritik, 30 gün veya daha az kalan repository uyarı olarak öne çıkar.

## Agent
Agent runtime/protokolü değişmedi.
