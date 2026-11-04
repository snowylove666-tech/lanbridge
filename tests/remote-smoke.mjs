import {spawn} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url));
const root=fs.mkdtempSync(path.join(here,'runtime-remote-')),port=49333;
const cached=fs.readdirSync(here).filter(x=>x.startsWith('runtime-remote-')).map(x=>path.join(here,x,'Data/Tools/cloudflared-2026.10.0.exe')).find(x=>fs.existsSync(x));
if(cached) {fs.mkdirSync(path.join(root,'Data/Tools'),{recursive:true});fs.copyFileSync(cached,path.join(root,'Data/Tools/cloudflared-2026.10.0.exe'));}
const exe=process.argv[2]?path.resolve(process.argv[2]):path.resolve(here,'../dist/LanBridge-v1.3.0.exe');
const child=spawn(exe,['--test-mode','--port',String(port),'--data',root],{windowsHide:true,stdio:'ignore'});
const sleep=ms=>new Promise(r=>setTimeout(r,ms)); let boot;
let computerChild;
async function control(action,extra={}) { return (await fetch(`http://127.0.0.1:${port}/test/control`,{method:'POST',headers:{Authorization:'Bearer '+boot.key,'Content-Type':'application/json'},body:JSON.stringify({action,...extra})})).json(); }
async function request(base,route,body,token) {
 for(let attempt=0;;attempt++) {
  const r=await fetch(base+route,{method:body===undefined?'GET':'POST',headers:{...(token?{Authorization:'Bearer '+token}:{}),...(body!==undefined?{'Content-Type':'application/json'}:{})},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(8000)});
  const text=await r.text();let data;try{data=JSON.parse(text);}catch{data={error:text.slice(0,300)};}
  if(body===undefined && [429,502,503,504].includes(r.status) && attempt<2){await sleep(800*(attempt+1));continue;}
  return {status:r.status,data};
 }
}
try {
 for(let i=0;i<80;i++) { const p=path.join(root,'Data/test-bootstrap.json'); if(fs.existsSync(p)) {boot=JSON.parse(fs.readFileSync(p,'utf8')); break;} await sleep(100); }
 assert.ok(boot); await control('start-remote'); let status;
 for(let i=0;i<90;i++) { await sleep(2000); status=await control('status'); if(i%5===0) console.log(status.remoteStatus); if(status.remote)break; if(status.remoteStatus.startsWith('跨网未开启'))throw new Error(status.remoteStatus); }
 assert.ok(status.remote,'Remote origin unavailable');
 const origin=status.remote;
 let ready=false;const verifyUntil=Date.now()+40000;
 while(Date.now()<verifyUntil) { try { const r=await request(origin,'/health'); if(r.status===200 && r.data.app==='LanBridge') {ready=true;break;} }catch{} await sleep(1000); }
 if(!ready) {
  const pending=await control('status');
  assert.equal(pending.remote,origin,'A failed local self-test must retain the created route for another device to try');
  assert.ok(!/^(已连接|已有设备加入|另一台设备已接入)/.test(pending.remoteStatus),'Pending route must not claim an actual device joined');
  console.log('PASS created route remains open after failed self-test and does not claim a device connected');
  throw new Error('Public HTTPS data path not verified on this network; created route was retained and pending status remained honest. '+pending.remoteDiagnostic);
 }
 console.log('PASS isolated public HTTPS health');
 assert.equal((await request(origin,'/v1/state')).status,401);
 assert.equal((await request(origin,'/v1/join',{name:'forbidden'})).status,403);
 assert.equal((await request(origin,'/v1/pair',{code:status.code})).status,403);
 const paired=await request(origin,'/v1/pair',{code:status.pair,name:'Generated remote test'}); assert.equal(paired.status,200);const token=paired.data.token;
 console.log('PASS remote QR pairing, rejects PIN and unapproved access');
 assert.equal((await request(origin,'/v1/sync',{text:'generated baseline',mode:'smart'},token)).data.action,'ready');
 await control('publish',{text:'generated Windows text'});
 const r=await request(origin,'/v1/sync',{text:'generated baseline',mode:'smart'},token);assert.equal(r.data.text,'generated Windows text');
 assert.equal((await request(origin,'/v1/ack',{receipt:r.data.receipt},token)).status,200);
 assert.equal((await request(origin,'/v1/sync',{text:'generated phone text',mode:'smart'},token)).data.action,'sent');
 console.log('PASS bidirectional generated text and acknowledgment over tunnel');
 const data=crypto.randomBytes(256*1024);
 const upload=await fetch(origin+'/v1/files?name=generated-remote.bin',{method:'POST',headers:{Authorization:'Bearer '+token,'Content-Type':'application/octet-stream'},body:data,signal:AbortSignal.timeout(30000)});assert.equal(upload.status,200,await upload.text());
 const snapshot=await request(origin,'/v1/state',undefined,token);assert.equal(snapshot.status,200,JSON.stringify(snapshot.data));const state=snapshot.data;const file=state.files.find(x=>x.name==='generated-remote.bin');assert.ok(file);
 const ticket=(await request(origin,'/v1/tickets/'+file.id,{},token)).data.url;
 const received=Buffer.from(await (await fetch(origin+ticket,{signal:AbortSignal.timeout(30000)})).arrayBuffer());assert.deepEqual(received,data);
 console.log('PASS 256 KB upload/download hash over tunnel');
 if(process.argv.includes('--computers')) {
  const computerRoot=fs.mkdtempSync(path.join(here,'runtime-remote-computer-')),computerPort=49343;
  computerChild=spawn(exe,['--test-mode','--port',String(computerPort),'--data',computerRoot],{windowsHide:true,stdio:'ignore'});
  let computerBoot;for(let i=0;i<80;i++){const p=path.join(computerRoot,'Data/test-bootstrap.json');if(fs.existsSync(p)){computerBoot=JSON.parse(fs.readFileSync(p));break;}await sleep(100);}assert.ok(computerBoot);
  const pcControl=async(action='status',extra={})=>{const r=await request(`http://127.0.0.1:${computerPort}`,'/test/control',{action,...extra},computerBoot.key);assert.equal(r.status,200,JSON.stringify(r.data));return r.data;};
  const wait=async(fn)=>{for(let i=0;i<100;i++){const r=await fn();if(r)return r;await sleep(250);}throw new Error('Native remote peer timeout');};
  status=await control('refresh');await pcControl('peer-connect',{url:origin+'/#pair='+status.pair});
  await wait(async()=>{const s=await pcControl();return s.peerConnected && s.peerStatus.startsWith('已连接');});
  console.log('PASS native Windows computer pairs through full HTTPS link');
  await control('set-auto',{enabled:true});await control('publish',{text:'Generated native HTTPS A to B'});
  await wait(async()=>(await pcControl()).desktop.text==='Generated native HTTPS A to B');
  await pcControl('publish',{text:'Generated native HTTPS B to A'});
  await wait(async()=>(await control('status')).desktop.text==='Generated native HTTPS B to A');
  console.log('PASS native automatic clipboard in both directions through HTTPS tunnel');
  const target=(await control('status')).devices.find(x=>x.kind==='computer');assert.ok(target);
  const generated=crypto.randomBytes(512*1024),p=path.join(root,'native-targeted.bin');fs.writeFileSync(p,generated);
  await control('target-file',{path:p,device:target.id});
  const received=await wait(async()=>(await pcControl()).files.find(x=>x.name==='native-targeted.bin'));
  assert.deepEqual(fs.readFileSync(received.path),generated);
  const outgoing=path.join(computerRoot,'native-upload.bin');fs.writeFileSync(outgoing,generated);await pcControl('peer-send',{path:outgoing});
  const uploaded=await wait(async()=>(await control('status')).files.find(x=>x.name==='native-upload.bin'));
  assert.deepEqual(fs.readFileSync(uploaded.path),generated);
  console.log('PASS native targeted files and direct uploads through HTTPS tunnel');
  await pcControl('peer-disconnect');computerChild.kill();
 }
 await control('start-remote');assert.equal((await control('status')).remote,'');
 assert.equal((await request(`http://127.0.0.1:${port}`,'/v1/state',undefined,token)).status,401);
 let closed=false;try{closed=(await fetch(origin+'/health',{signal:AbortSignal.timeout(12000)})).status!==200;}catch{closed=true;}assert.ok(closed);
 console.log('PASS tunnel closes and local health remains available');
 fs.writeFileSync(path.join(here,'remote-result.json'),JSON.stringify({passed:true,nativeComputers:process.argv.includes('--computers'),executable:exe,runtime:root,at:new Date().toISOString()},null,2));
} catch(error) {
 fs.writeFileSync(path.join(here,'remote-result.json'),JSON.stringify({passed:false,nativeComputers:process.argv.includes('--computers'),executable:exe,runtime:root,error:String(error.message),at:new Date().toISOString()},null,2));
 throw error;
} finally {
 if(computerChild?.exitCode===null)computerChild.kill();
 if(boot)try {const status=await control('status');if(status.remote || !status.remoteStatus.includes('关闭') && !status.remoteStatus.startsWith('跨网未开启')) await control('start-remote');}catch{}
 child.kill();
}
