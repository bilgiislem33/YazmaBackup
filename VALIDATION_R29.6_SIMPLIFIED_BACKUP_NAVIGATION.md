# R29.6 Sade Yedekleme Menüsü

Günlük kullanıcı menüsü gerçek yedekleme akışındaki yedi ekrana indirildi:

1. Genel Bakış
2. Bilgisayarlar — bilgisayar ve kullanıcı/sahip adları
3. Yedekleme Oluştur — kaynak klasör ve zamanlama
4. Yedekleme Geçmişi — başarılı ve hatalı işler
5. Geri Yükleme — dosya ve klasör kurtarma
6. Depolama & NAS — NAS ayarı ve gerçek bağlantı testi
7. MeshCentral — Agent dağıtımı

Tahmin, otonom iyileştirme, HA/DR ve yönetici raporlama motorları backend ve kaynak koddan silinmedi. Bunlar günlük operatör menüsünden çıkarıldı; böylece çekirdek yetenek kaybı veya veri şeması değişikliği olmadan arayüz sadeleştirildi.

Komut paleti de aynı yedi ekranı kullanır. Gizlenen Operasyon Merkezi'ne geçiş yapan NOC üst düğmesi kaldırıldı ve `Ctrl+K` ifadesi son kullanıcı için `Hızlı Menü` olarak değiştirildi.

Merkezi frontend kalite kapısı, sol menünün tam olarak bu yedi kimliği içerdiğini ve yeni bir karmaşık modülün yanlışlıkla ana menüye eklenmediğini doğrular.
