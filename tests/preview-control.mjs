import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.join(path.dirname(fileURLToPath(import.meta.url)),'runtime-preview');
const boot=JSON.parse(fs.readFileSync(path.join(root,'Data/test-bootstrap.json'),'utf8'));
async function control(action,extra={}) { const response=await fetch(`http://127.0.0.1:${boot.port}/test/control`,{method:'POST',headers:{Authorization:`Bearer ${boot.key}`,'Content-Type':'application/json'},body:JSON.stringify({action,...extra})});if(!response.ok)throw new Error(await response.text());return response.json(); }
await control('publish',{text:'从 Windows 复制，在 iPhone 接着用。\n你好，轻桥 👋'});
const sample=path.join(root,'轻桥 · 开始使用.txt');
fs.writeFileSync(sample,'欢迎使用轻桥！\nWindows 与 iPhone 连接同一 Wi-Fi，扫码后即可互传文件。\n');
await control('add-file',{path:sample});
console.log(JSON.stringify({code:(await control('refresh')).code}));
