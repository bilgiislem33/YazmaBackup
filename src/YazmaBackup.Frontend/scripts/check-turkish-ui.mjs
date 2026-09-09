import {readFileSync} from "node:fs";
import {resolve} from "node:path";

const root=resolve(import.meta.dirname,"..");
const source=readFileSync(resolve(root,"components","console.tsx"),"utf8");
const login=readFileSync(resolve(root,"components","login-view.tsx"),"utf8");
const passwordChange=readFileSync(resolve(root,"components","password-change-view.tsx"),"utf8");
const home=readFileSync(resolve(root,"app","page.tsx"),"utf8");
const dictionary=readFileSync(resolve(root,"lib","ui-strings.ts"),"utf8");
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
console.log("PASS: Merkezi Türkçe arayüz sözlüğü ve enterprise modül dil kapısı.");
