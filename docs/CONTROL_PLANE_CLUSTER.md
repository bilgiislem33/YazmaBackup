# Control Plane Leadership — v1.1.0

v1.1.0 scheduler ve operational sentinel için `lease_name + owner_id + lease_id + monotonic epoch + expiry` modeli getirir. Bu, cluster davranış sözleşmesini ve split-brain'e karşı fencing temelini tanımlar.

**Önemli sınır:** paket içindeki aktif runtime store halen atomik JSON state dosyasıdır. JSON store birden fazla Control Plane process'i için production-grade distributed transaction garantisi vermez. Bu nedenle v1.1.0 leadership modeli tek-node/pilot doğrulaması ve PostgreSQL cutover sözleşmesi içindir; gerçek multi-node HA, normalized PostgreSQL runtime store etkinleşmeden `productionReady=true` olamaz.

PostgreSQL migration `004_enterprise_control_plane.sql` aynı lease modelini gerçek ortak veritabanına taşımak için `cluster_leases` tablosunu hazırlar.
