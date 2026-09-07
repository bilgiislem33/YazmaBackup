# MeshCentral Entegrasyon Katmanı

v1.1.0 YazmaBackup Agent kimliği ile MeshCentral Node ID arasında merkezi, denetlenebilir eşleme tutar. MeshCentral API endpoint biçimleri bu oturumda dış kaynakla doğrulanamadığı için ürün doğrulanmamış API çağrısı veya node URL formatı tahmin etmez.

Bu aşamada amaç:

1. Hangi YazmaBackup Agent'ın hangi MeshCentral node'una karşılık geldiğini kesin olarak kaydetmek.
2. MeshCentral üzerinden mevcut sessiz Agent dağıtım scriptlerini kullanmaya devam etmek.
3. Bir sonraki doğrulanmış entegrasyonda node health / deployment result bilgisini bu sabit eşleme üzerinden bağlamak.

Eşleme yalnız HTTPS base URI kabul eder. Node ID opaque identifier olarak saklanır; yeniden biçimlendirilmez.
