import {spawn,spawnSync} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url)),port=49333;
const root=fs.mkdtempSync(path.join(here,'runtime-remote-diag-'));
const helper=path.join(root,'https-diagnostic.exe');
const compiled=spawnSync(path.join(process.env.WINDIR,'Microsoft.NET/Framework64/v4.0.30319/csc.exe'),['/nologo','/target:exe','/codepage:65001','/utf8output','/out:'+helper,path.join(here,'https-diagnostic.cs')],{windowsHide:true,encoding:'utf8'});
if(compiled.status!==0)throw new Error(compiled.stdout+compiled.stderr);
const cached=fs.readdirSync(here).filter(x=>x.startsWith('runtime-remote-')).map(x=>path.join(here,x,'Data/Tools/cloudflared-2026.10.0.exe')).find(x=>fs.existsSync(x));
if(cached){fs.mkdirSync(path.join(root,'Data/Tools'),{recursive:true});fs.copyFileSync(cached,path.join(root,'Data/Tools/cloudflared-2026.10.0.exe'));}
const child=spawn(path.resolve(here,'../dist/LanBridge-v1.4.0-test.exe'),['--test-mode','--port',String(port),'--data',root],{windowsHide:true,stdio:'ignore'});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));let boot;
async function control(action){return(await fetch(`http://127.0.0.1:${port}/test/control`,{method:'POST',headers:{Authorization:'Bearer '+boot.key,'Content-Type':'application/json'},body:JSON.stringify({action})})).json();}
try{
 for(let i=0;i<80;i++){const p=path.join(root,'Data/test-bootstrap.json');if(fs.existsSync(p)){boot=JSON.parse(fs.readFileSync(p));break;}await sleep(100);}
 await control('start-remote');let origin;
 for(let i=0;i<90;i++){const s=await control('status');if(s.remoteCandidate||s.remote){origin=s.remoteCandidate||s.remote;break;}if(s.remoteStatus.startsWith('跨网未开启'))throw new Error(s.remoteStatus);await sleep(1500);}
 if(!origin)throw new Error('No candidate');
 console.log('Candidate route:',origin);
 try{const response=await fetch(origin+'/health',{signal:AbortSignal.timeout(10000)});console.log('NODE',response.status,(await response.text()).slice(0,250));}catch(e){console.log('NODE ERROR',String(e),String(e.cause));}
 const probe=spawnSync(helper,[origin+'/health','https://example.com/'],{windowsHide:true,encoding:'utf8',timeout:40000});console.log(probe.stdout,probe.stderr);
 console.log('FINAL', (await control('status')).remoteStatus);
}finally{if(boot)try{await control('start-remote');}catch{}child.kill();}
