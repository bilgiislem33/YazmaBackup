export const uiText = {
  disasterRecovery: {
    eyebrow: "R27.2 · Kurtarma Deneyimi",
    title: "Felaket Kurtarma Operasyon Odası",
    start: "Operasyon Odasını Başlat",
    empty: "Henüz felaket kurtarma oturumu yok. Yeni operasyon odası başlatıldığında gerçek kurtarma bağımlılık grafiği burada görünecek.",
    rail: "Bağımlılık Sıralı Kurtarma Akışı",
    liveGraph: "CANLI AKIŞ",
    recoveryStep: "Kurtarma Adımı",
    currentGate: "Geçerli Kontrol Noktası",
    session: "Oturum"
  },
  autonomous: {
    safetyPolicy: "Otonom Güvenlik Politikası",
    failureQueue: "Hata Sınıflandırma Kuyruğu",
    stateMachine: "Düzeltme Durum Akışı",
    approveSelfHeal: "Otomatik Düzeltmeyi Onayla",
    noRuns: "Düzeltme işlemi yok.",
    newCanaryRollout: "Yeni Kontrollü Pilot Dağıtım",
    rolloutName: "Dağıtım adı",
    targetVersion: "Hedef sürüm",
    signature: "ECDSA imzası (Base64)",
    canaryPercent: "Pilot grup %",
    maxParallel: "En fazla eşzamanlı",
    observationMinutes: "Gözlem süresi (dk)",
    minimumScore: "En düşük puan",
    backupSlo: "Yedekleme hizmet hedefi %",
    agentOnline: "Çevrimiçi Agent %",
    createDraft: "Dağıtım Taslağı Oluştur",
    startCanary: "Ön Kontrol ve Pilot Dağıtımı Başlat",
    resumeAfterHealth: "Sağlık Kontrolünden Sonra Sürdür",
    rollbackSemantics: "Geri Dönüş Kuralları",
    installFailure: "Kurulum hatası",
    canaryHealthFailure: "Pilot grup sağlık hatası",
    statePersistence: "Durumun kalıcı saklanması",
    autonomousDiagnosis: "Otonom tanılama",
    mutationApproval: "Değişiklik onayı",
    rolloutGate: "Dağıtım kontrolü",
    rollback: "Geri dönüş",
    pass: "GEÇTİ",
    hold: "BEKLETİLİYOR",
    failSafe: "Güvenli Durdurma",
    silentMutationOn: "Sessiz Değişiklik Açık"
  },
  diagnostics: {
    matrix: "Üretim Tanılama Matrisi",
    controlPlaneHealth: "Kontrol Katmanı Operasyon Sağlığı",
    clusterScheduler: "Küme Politika Zamanlayıcısı",
    backupSlo: "Yedekleme Hizmet Hedefi",
    agentConnectivity: "Agent Bağlantısı",
    repositoryHealth: "Depo Sağlığı",
    meshFleet: "MeshCentral Filosu"
  },
  executive: {
    eyebrow: "Yönetici Koruma Özeti",
    boardStatus: "Üst Yönetim Koruma Durumu",
    activePolicies: "Etkin yedekleme politikaları",
    recoveryEvidence: "Kurtarma kanıtı"
  },
  fleet: {
    liveMap: "Canlı Filo Görünümü",
    live: "CANLI FİLO",
    device360: "Cihaz 360",
    activePolicies: "Etkin Yedekleme Politikaları",
    active: "ETKİN",
    protection: "Koruma Durumu"
  },
  notifications: {
    route: "Bildirim Hedefi",
    create: "Bildirim Hedefi Oluştur",
    created: "Bildirim hedefi oluşturuldu.",
    empty: "Bildirim hedefi yok.",
    name: "Hedef adı",
    secret: "HMAC gizli anahtarı (32-256 karakter)",
    deliveries: "Bildirim Teslimat Geçmişi",
    safeRouteCount: "güvenli hedef",
    hostAllowlistError: "Hedef oluşturulamadı. İzin verilen sunucu listesini doğrulayın.",
    deleteConfirm: " bildirim hedefi silinecek. Devam edilsin mi?"
  }
} as const;

const statusLabels: Record<string, string> = {
  active: "Etkin",
  approved: "Onaylandı",
  "awaiting-approval": "Onay Bekliyor",
  cancelled: "İptal Edildi",
  completed: "Tamamlandı",
  diagnosing: "Tanılanıyor",
  draft: "Taslak",
  failed: "Başarısız",
  held: "Bekletiliyor",
  applying: "Uygulanıyor",
  offline: "Çevrimdışı",
  online: "Çevrimiçi",
  pending: "Bekliyor",
  "queued-diagnosis": "Tanılama Kuyruğunda",
  running: "Çalışıyor",
  staged: "Hazırlandı",
  verifying: "Doğrulanıyor",
  verified: "Doğrulandı"
};

const severityLabels: Record<string, string> = {
  critical: "Kritik",
  warning: "Uyarı",
  info: "Bilgi"
};

export function translateStatus(value?: string | null): string {
  if (!value) return "—";
  return statusLabels[value.toLocaleLowerCase("en-US")] ?? value;
}

export function translateSeverity(value?: string | null): string {
  if (!value) return "—";
  return severityLabels[value.toLocaleLowerCase("en-US")] ?? value;
}
