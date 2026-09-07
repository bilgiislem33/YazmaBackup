import * as React from "react";
import { cn } from "@/lib/utils";
type Props=React.ButtonHTMLAttributes<HTMLButtonElement>&{variant?:"default"|"outline"|"ghost"|"danger";size?:"sm"|"md"|"lg"};
export function Button({className,variant="default",size="md",...props}:Props){
 const variants={
  default:"bg-slate-900 text-white hover:bg-slate-800 shadow-sm",
  outline:"border border-slate-200 bg-white text-slate-700 hover:bg-slate-50",
  ghost:"text-slate-600 hover:bg-slate-100",
  danger:"bg-rose-600 text-white hover:bg-rose-700"
 };
 const sizes={sm:"h-9 px-3 text-xs",md:"h-11 px-4 text-sm",lg:"h-12 px-5 text-sm"};
 return <button className={cn("inline-flex items-center justify-center gap-2 rounded-xl font-semibold transition disabled:cursor-not-allowed disabled:opacity-50",variants[variant],sizes[size],className)} {...props}/>;
}
