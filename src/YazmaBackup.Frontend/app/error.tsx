"use client";
import {useEffect} from "react";
import {AlertTriangle,RefreshCw} from "lucide-react";
export default function GlobalError({error,reset}:{error:Error&{digest?:string};reset:()=>void}){
 useEffect(()=>{console.error("YazmaBackup global UI error",error)},[error]);
 return <main className="grid min-h-screen place-items-center bg-slate-50 p-6"><section role="alert" className="w-full max-w-xl rounded-[28px] border border-amber-100 bg-white p-8 shadow-xl"><AlertTriangle className="mb-4 text-amber-500"/><h1 className="text-xl font-black text-slate-900">Yönetim arayüzü güvenli moda geçti</h1><p className="mt-2 text-sm leading-6 text-slate-500">Arayüz hatası izole edildi. Control Plane ve Agent işlemleri bu ekran hatası nedeniyle durdurulmaz.</p><button onClick={reset} className="mt-6 inline-flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-bold text-white"><RefreshCw size={16}/>Arayüzü yeniden dene</button></section></main>;
}
