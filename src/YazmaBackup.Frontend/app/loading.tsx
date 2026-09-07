export default function Loading(){
 return <main role="status" aria-label="YazmaBackup yükleniyor" className="min-h-screen bg-slate-50 p-6"><div className="mx-auto max-w-[1600px] animate-pulse space-y-6"><div className="h-20 rounded-3xl bg-slate-100"/><div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">{[0,1,2,3].map(x=><div key={x} className="h-32 rounded-2xl bg-slate-100"/>)}</div><div className="h-[520px] rounded-3xl bg-slate-100"/></div></main>;
}
