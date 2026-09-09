# Türkçe Arayüz Standardı

R29.2 ile kullanıcıya gösterilen kurumsal modül metinleri merkezi `lib/ui-strings.ts` sözlüğüne taşınmaya başlanmıştır.

## Uygulanan kurallar

- Başlık, işlem düğmesi, form etiketi ve bildirimlerde doğal Türkçe kullanılır.
- API sözleşmelerindeki İngilizce durum ve önem kodları değiştirilmez; arayüzde `translateStatus` ve `translateSeverity` ile Türkçe gösterilir.
- Agent, API, NAS, RTO, SHA-256, ECDSA ve ImagePath gibi ürün/protokol isimleri teknik anlamı korumak için çevrilmez.
- Yeni kullanıcı metinleri mümkün olduğunda merkezi sözlüğe eklenir.
- `npm run localization:check`, bilinen İngilizce veya karma dil ifadelerinin `console.tsx` içine geri dönmesini engeller.

## Bu sürümde merkezileştirilen alanlar

- Felaket kurtarma operasyon odası ve bağımlılık sıralı kurtarma akışı
- Otonom güvenlik, hata sınıflandırma ve düzeltme durum akışı
- Kontrollü pilot dağıtım ve geri dönüş kuralları
- Üretim tanılama matrisi
- Yönetici koruma özeti
- Canlı filo ve Cihaz 360
- Bildirim hedefleri, teslimat geçmişi ve kullanıcı bildirimleri
- Yeni kurumsal modüllerin sol menü adları
