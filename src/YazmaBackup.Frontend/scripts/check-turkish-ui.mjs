import {readFileSync} from "node:fs";
import {resolve} from "node:path";

const root=resolve(import.meta.dirname,"..");
const source=readFileSync(resolve(root,"components","console.tsx"),"utf8");
const login=readFileSync(resolve(root,"components","login-view.tsx"),"utf8");
const passwordChange=readFileSync(resolve(root,"components","password-change-view.tsx"),"utf8");
const home=readFileSync(resolve(root,"app","page.tsx"),"utf8");
const dictionary=readFileSync(resolve(root,"lib","ui-strings.ts"),"utf8");
const navSource=source.slice(source.indexOf("const nav=["),source.indexOf("];",source.indexOf("const nav=["))+2);
const forbidden=[
  "Autonomous Safety Policy","Failure Classification Queue","Remediation State Machine",
  "Production Diagnostic Matrix","Executive Protection Brief","Board-Level Protection Status",
  "Live Fleet Map","Dependency Recovery Rail","Device 360","War Room Başlat",
  "Self-Heal Onayla","Yeni Canary Rollout","Health Gate Sonrası Resume",
  "Rollback Semantiği","Max paralel","Observation dk","Min score",
  "Notification route oluşturuldu.","Notification Route Oluştur","Notification Route"
];
const found=forbidden.filter(text=>source.includes(text));
if(found.length){
  console.error("Türkçe arayüz kalite kapısı başarısız. Kalan ifadeler:",found.join(", "));
  process.exit(1);
}
for(const marker of ["uiText.autonomous","uiText.diagnostics","uiText.executive","uiText.fleet","uiText.notifications","translateStatus","translateSeverity"]){
  if(!source.includes(marker)){
    console.error("Merkezi Türkçe sözlük kullanımı eksik:",marker);
    process.exit(1);
  }
}
for(const translation of ["Otonom Güvenlik Politikası","Üretim Tanılama Matrisi","Yönetici Koruma Özeti","Canlı Filo Görünümü","Bildirim Hedefi"]){
  if(!dictionary.includes(translation)){
    console.error("Zorunlu Türkçe sözlük girdisi eksik:",translation);
    process.exit(1);
  }
}
for(const invariant of [[login,"response.user"],[home,"user.mustChangePassword"],[home,"PasswordChangeView"],[passwordChange,"/api/v1/session/change-password"],[passwordChange,"Parola değişikliği zorunlu"]]){
  if(!invariant[0].includes(invariant[1])){
    console.error("Zorunlu ilk parola değişikliği akışı eksik:",invariant[1]);
    process.exit(1);
  }
}
for(const invariant of ["/api/v1/admin/agents/","/browse","pollCommandResult<BrowseResult>","sourcePaths","/api/v1/admin/policies/multi-source","Bu Klasörü Seç"]){
  if(!source.includes(invariant)){
    console.error("Çoklu kaynak klasör seçimi akışı eksik:",invariant);
    process.exit(1);
  }
}
if(!source.includes("type EnqueueResponse={commandId:string}") || source.includes("api<Enqueue>")){
  console.error("Agent komut yanıt tipi ortak kapsamda değil veya eski kapsam-dışı Enqueue kullanımı kaldı.");
  process.exit(1);
}
for(const invariant of ["Kaydet ve Bağlantıyı Test Et","Kayıtlı Ayarları Test Et","/nas-access-test","pollCommandResult<NasTestResult>","directoryReadable","writeProbeSucceeded","Sistem kimliği:"]){
  if(!source.includes(invariant)){
    console.error("Kolay NAS kurulumu veya gerçek bağlantı testi eksik:",invariant);
    process.exit(1);
  }
}
if(!source.includes("depo bekleme kilidi otomatik kaldırıldı") || !source.includes("Otomatik kilit iyileştirme")){
  console.error("Başarılı NAS testi sonrası repository circuit iyileştirme açıklaması eksik.");
  process.exit(1);
}
for(const invariant of ["function agentDisplayName(agent:Agent)","assignedUser?.trim()","<OperationsPage agents={agents}/>","Kullanıcı / Bilgisayar","agentDisplayName(a)"]){
  if(!source.includes(invariant)){
    console.error("Atanmış kullanıcı adının politika ve operasyon ekranlarına taşınması eksik:",invariant);
    process.exit(1);
  }
}
const simpleNavIds=["dashboard","agents","policies","operations","restore","storage","mesh"];
for(const id of simpleNavIds){
  if(!navSource.includes(`id:"${id}"`)){
    console.error("Sade yedekleme menüsünde zorunlu ekran eksik:",id);
    process.exit(1);
  }
}
const navItemCount=(navSource.match(/\{id:/g)||[]).length;
if(navItemCount!==simpleNavIds.length){
  console.error("Sol menü yeniden kalabalıklaştırılmış. Beklenen/Gerçek:",simpleNavIds.length,navItemCount);
  process.exit(1);
}
for(const invariant of ["assigned-user\",{method:\"PUT\"","onAgentUpdated(updated)","onNasUpdated(r.profile)","agentRepositoryRoot(nas.repositoryRoot,agent)","Otomatik yedekleme hedefi","value={repositoryRoot} readOnly","önce Depolama & NAS ekranında"]){
  if(!source.includes(invariant)){
    console.error("Kalıcı bilgisayar adı veya otomatik kişi bazlı NAS hedefi eksik:",invariant);
    process.exit(1);
  }
}
console.log("PASS: Merkezi Türkçe arayüz sözlüğü ve enterprise modül dil kapısı.");
