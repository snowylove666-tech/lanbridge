import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import assert from 'node:assert/strict';
import {spawn,spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url)),runtime=fs.mkdtempSync(path.join(here,'runtime-verify-'));
const exe=process.argv[2]?path.resolve(process.argv[2]):path.resolve(here,'../dist/LanBridge-v1.4.0.exe');
const helper=path.join(runtime,'verify.exe'),compiler=path.join(process.env.WINDIR,'Microsoft.NET/Framework64/v4.0.30319/csc.exe');
const built=spawnSync(compiler,['/nologo','/target:exe','/codepage:65001','/utf8output','/out:'+helper,path.join(here,'tunnel-verification.cs')],{windowsHide:true,encoding:'utf8'});assert.equal(built.status,0,built.stdout+built.stderr);
let mode='valid';
const server=http.createServer((req,res)=>{const url=new URL(req.url,'http://localhost');const payload={app:'LanBridge',instance:'generated-instance',nonce:url.searchParams.get('nonce')};if(mode==='wrong-nonce')payload.nonce='wrong';if(mode==='wrong-instance')payload.instance='wrong';if(mode==='redirect'){res.writeHead(302,{Location:'/health'}).end();return;}if(mode==='html'){res.end('<html>upstream error</html>');return;}if(mode==='oversized'){res.end('x'.repeat(9000));return;}res.setHeader('Content-Type','application/json');res.end(JSON.stringify(payload));});
await new Promise(r=>server.listen(0,'127.0.0.1',r));const origin='http://127.0.0.1:'+server.address().port;
let checks=0;
try{for(const test of ['valid','wrong-nonce','wrong-instance','html','oversized','redirect']){mode=test;const status=await new Promise((resolve,reject)=>{const child=spawn(helper,[exe,origin,'generated-instance'],{windowsHide:true,stdio:'ignore'});child.once('error',reject);child.once('exit',resolve);});assert.equal(status===0,test==='valid',test);checks++;console.log('PASS public route verification '+test);}fs.writeFileSync(path.join(here,'tunnel-verification-result.json'),JSON.stringify({passed:true,checks,executable:exe,at:new Date().toISOString()},null,2));}finally{await new Promise(r=>server.close(r));}
