"use client";
import {FormEvent,useState} from "react";
import {ShieldCheck,LockKeyhole} from "lucide-react";
import {api} from "@/lib/api";
import {Button} from "@/components/ui/button";
import {Input} from "@/components/ui/input";
import type {LoginResponse,SessionUser} from "@/lib/types";

export function LoginView({onLogin}:{onLogin:(u:SessionUser)=>void}){
 const [username,setUsername]=useState(""),[password,setPassword]=useState(""),[error,setError]=useState(""),[busy,setBusy]=useState(false);
 async function submit(e:FormEvent){e.preventDefault();setBusy(true);setError("");try{const response=await api<LoginResponse>("/api/v1/session/login",{method:"POST",body:JSON.stringify({username,password})});onLogin(response.user);}catch{setError("Kullanıcı adı veya parola geçersiz.");}finally{setBusy(false)}}
 return <main className="min-h-screen grid place-items-center p-6">
  <section className="yb-glass yb-shell-shadow w-full max-w-md rounded-3xl border border-white p-8">
   <div className="mb-7 flex items-center gap-4"><div className="grid h-14 w-14 place-items-center rounded-2xl bg-slate-900 text-white shadow-lg"><ShieldCheck size={27}/></div><div><div className="text-xl font-black tracking-tight text-slate-900">Yazma<span className="text-blue-600">Backup</span></div><div className="text-xs font-semibold text-slate-400">Enterprise Data Protection</div></div></div>
   <div className="mb-6"><span className="inline-flex rounded-full border border-blue-100 bg-blue-50 px-3 py-1 text-[11px] font-bold text-blue-700">Yeni Yönetim Merkezi</span><h1 className="mt-4 text-3xl font-black tracking-tight text-slate-900">Hoş geldiniz</h1><p className="mt-2 text-sm leading-6 text-slate-500">Bilgisayarlarınızı, politikaları ve yedek sağlığını tek kurumsal konsoldan yönetin.</p></div>
   <form onSubmit={submit} className="space-y-4"><label className="block text-xs font-bold text-slate-600">Kullanıcı adı<Input value={username} onChange={e=>setUsername(e.target.value)} className="mt-2" autoComplete="username" required/></label><label className="block text-xs font-bold text-slate-600">Parola<Input value={password} onChange={e=>setPassword(e.target.value)} className="mt-2" type="password" autoComplete="current-password" required/></label>{error&&<p className="rounded-xl bg-rose-50 p-3 text-xs font-semibold text-rose-700">{error}</p>}<Button className="w-full" size="lg" disabled={busy}><LockKeyhole size={16}/>{busy?"Giriş yapılıyor...":"Giriş Yap"}</Button></form>
  </section>
 </main>;
}
