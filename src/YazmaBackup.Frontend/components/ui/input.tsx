import * as React from "react";
import { cn } from "@/lib/utils";
export function Input({className,...props}:React.InputHTMLAttributes<HTMLInputElement>){return <input className={cn("h-11 w-full rounded-xl border border-slate-200 bg-white px-3 text-sm text-slate-900 outline-none transition placeholder:text-slate-400 focus:border-blue-300 focus:ring-4 focus:ring-blue-50",className)} {...props}/>;}
