# YazmaBackup v1.2.0 İlerleme Raporu

v1.2.0 küçük bir bakım sürümü değil, MeshCentral yönetimini manuel Node eşlemesinden gerçek Fleet Connector modeline taşıyan büyük mimari geçiştir.

| Alan | v1.1 R11 | v1.2.0 R1 | Değişim |
|---|---:|---:|---:|
| Kaynak / mimari geliştirme | 96% | 98% | +2 |
| Güvenlik | 96% | 98% | +2 |
| Test / kalite kapıları | 95% | 96%* | +1 |
| Windows gerçek ortam doğrulaması | 88% | 88% | 0 |
| NAS / restore doğrulaması | 86% | 86% | 0 |
| Merkezi yönetim | 92% | 97% | +5 |
| Üretim / pilot hazırlığı | 91% | 93% | +2 |
| Ticari kurumsal ürün seviyesi | 90% | 94% | +4 |

`*` Kaynak/statik kapılar yükseltildi; v1.2 C# build/self-test bu çalışma konteynerinde .NET SDK bulunmadığı için Windows VERIFY kanıtı bekliyor.

MeshCentral Fleet Connector gerçek saha doğrulaması: **0% → kanıt bekliyor**. İlk gerçek bağlantı ve test-PC dağıtımı tamamlanmadan bu oran artırılmamalıdır.
