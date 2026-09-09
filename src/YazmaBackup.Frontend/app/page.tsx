"use client";
import {useEffect,useState} from "react";
import {api} from "@/lib/api";
import type {SessionUser} from "@/lib/types";
import {LoginView} from "@/components/login-view";
import {PasswordChangeView} from "@/components/password-change-view";
import {Console} from "@/components/console";

export default function Home(){
 const [user,setUser]=useState<SessionUser|null|undefined>(undefined);
 useEffect(()=>{api<SessionUser>("/api/v1/session/me").then(setUser).catch(()=>setUser(null))},[]);
 if(user===undefined)return <div className="grid min-h-screen place-items-center text-sm font-semibold text-slate-400">YazmaBackup hazırlanıyor…</div>;
 if(!user)return <LoginView onLogin={setUser}/>;
 if(user.mustChangePassword)return <PasswordChangeView user={user} onChanged={()=>setUser(null)}/>;
 return <Console user={user} onLogout={()=>setUser(null)}/>;
}
