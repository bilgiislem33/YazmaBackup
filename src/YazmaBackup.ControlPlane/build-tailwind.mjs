import fs from 'node:fs';
import { compile } from 'tailwindcss';
const source=fs.readFileSync(new URL('./wwwroot/tailwind.src.css',import.meta.url),'utf8');
const candidates=JSON.parse(fs.readFileSync(new URL('./tailwind-candidates.json',import.meta.url),'utf8'));
const compiler=await compile(source);
fs.writeFileSync(new URL('./wwwroot/tailwind.css',import.meta.url),compiler.build(candidates));
console.log(`Tailwind CSS generated (${candidates.length} candidates).`);
