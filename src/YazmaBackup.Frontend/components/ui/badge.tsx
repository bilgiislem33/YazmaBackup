import * as React from "react";
import { cn } from "@/lib/utils";
type Tone="success"|"warning"|"danger"|"neutral"|"info";
type Variant="default"|"destructive"|"secondary"|"outline";
export function Badge({className,tone,variant,...props}:React.HTMLAttributes<HTMLSpanElement>&{tone?:Tone;variant?:Variant}){
 const mapped:Tone=tone??(variant==="destructive"?"danger":variant==="default"?"success":variant==="secondary"?"neutral":variant==="outline"?"neutral":"neutral");
 const tones:Record<Tone,string>={success:"border-emerald-200 bg-emerald-50 text-emerald-700",warning:"border-amber-200 bg-amber-50 text-amber-700",danger:"border-rose-200 bg-rose-50 text-rose-700",neutral:"border-slate-200 bg-slate-50 text-slate-600",info:"border-blue-200 bg-blue-50 text-blue-700"};
 return <span className={cn("inline-flex items-center rounded-full border px-2.5 py-1 text-[11px] font-bold",tones[mapped],className)} {...props}/>;
}
