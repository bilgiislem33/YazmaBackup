import * as React from "react";
import { cn } from "@/lib/utils";
export function Card({className,...props}:React.HTMLAttributes<HTMLDivElement>){return <div className={cn("yb-card rounded-2xl",className)} {...props}/>;}
export function CardHeader({className,...props}:React.HTMLAttributes<HTMLDivElement>){return <div className={cn("flex items-start justify-between gap-4 p-5 pb-3",className)} {...props}/>;}
export function CardTitle({className,...props}:React.HTMLAttributes<HTMLHeadingElement>){return <h3 className={cn("m-0 text-base font-bold tracking-tight text-slate-900",className)} {...props}/>;}
export function CardDescription({className,...props}:React.HTMLAttributes<HTMLParagraphElement>){return <p className={cn("mt-1 text-sm text-slate-500",className)} {...props}/>;}
export function CardContent({className,...props}:React.HTMLAttributes<HTMLDivElement>){return <div className={cn("p-5 pt-2",className)} {...props}/>;}
