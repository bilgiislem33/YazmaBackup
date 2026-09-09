"use client";
import {FormEvent,useState} from "react";
import {KeyRound,ShieldCheck} from "lucide-react";
import {api} from "@/lib/api";
import {Button} from "@/components/ui/button";
import {Input} from "@/components/ui/input";
import type {SessionUser} from "@/lib/types";

export function PasswordChangeView({user,onChanged}:{user:SessionUser;onChanged:()=>void}){
 const [currentPassword,setCurrentPassword]=useState(""),[newPassword,setNewPassword]=useState(""),[confirmation,setConfirmation]=useState(""),[error,setError]=useState(""),[busy,setBusy]=useState(false);
 async function submit(e:FormEvent){
  e.preventDefault();setError("");
  if(newPassword.length<12){setError("Yeni parola en az 12 karakter olmalıdır.");return}
  if(newPassword!==confirmation){setError("Yeni parola ve doğrulaması eşleşmiyor.");return}
  if(currentPassword===newPassword){setError("Yeni parola geçici paroladan farklı olmalıdır.");return}
  setBusy(true);
  try{
   await api("/api/v1/session/change-password",{method:"POST",body:JSON.stringify({currentPassword,newPassword})});
   onChanged();
  }catch(e){setError(e instanceof Error?e.message:"Parola değiştirilemedi.")}finally{setBusy(false)}
 }
 return <main className="min-h-screen grid place-items-center p-6"><section className="yb-glass yb-shell-shadow w-full max-w-md rounded-3xl border border-white p-8">
  <div className="mb-7 flex items-center gap-4"><div className="grid h-14 w-14 place-items-center rounded-2xl bg-slate-900 text-white shadow-lg"><ShieldCheck size={27}/></div><div><div className="text-xl font-black tracking-tight text-slate-900">Yazma<span className="text-blue-600">Backup</span></div><div className="text-xs font-semibold text-slate-400">Güvenli İlk Kurulum</div></div></div>
  <div className="mb-6"><span className="inline-flex rounded-full border border-amber-100 bg-amber-50 px-3 py-1 text-[11px] font-bold text-amber-700">Parola değişikliği zorunlu</span><h1 className="mt-4 text-3xl font-black tracking-tight text-slate-900">Yeni parolanızı belirleyin</h1><p className="mt-2 text-sm leading-6 text-slate-500">{user.username} hesabının geçici parolası değiştirilmeden yönetim ekranlarına erişilemez.</p></div>
  <form onSubmit={submit} className="space-y-4"><label className="block text-xs font-bold text-slate-600">Geçici parola<Input value={currentPassword} onChange={e=>setCurrentPassword(e.target.value)} className="mt-2" type="password" autoComplete="current-password" required/></label><label className="block text-xs font-bold text-slate-600">Yeni parola<Input value={newPassword} onChange={e=>setNewPassword(e.target.value)} className="mt-2" type="password" autoComplete="new-password" minLength={12} required/></label><label className="block text-xs font-bold text-slate-600">Yeni parola doğrulaması<Input value={confirmation} onChange={e=>setConfirmation(e.target.value)} className="mt-2" type="password" autoComplete="new-password" minLength={12} required/></label>{error&&<p className="rounded-xl bg-rose-50 p-3 text-xs font-semibold text-rose-700">{error}</p>}<Button className="w-full" size="lg" disabled={busy}><KeyRound size={16}/>{busy?"Parola değiştiriliyor...":"Parolayı Değiştir"}</Button></form>
 </section></main>;
}
