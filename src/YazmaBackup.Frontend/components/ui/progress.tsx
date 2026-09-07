import {cn} from "@/lib/utils";
export function Progress({value,className}:{value:number;className?:string}){const v=Math.max(0,Math.min(100,value));return <div className={cn("h-2 overflow-hidden rounded-full bg-slate-100",className)}><div className="h-full rounded-full bg-gradient-to-r from-blue-600 to-cyan-500 transition-all" style={{width:`${v}%`}}/></div>;}
