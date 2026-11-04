import dgram from 'node:dgram';
import os from 'node:os';
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url));
const root=fs.mkdtempSync(path.join(here,'runtime-mdns-')),port=49335;
const child=spawn(path.resolve(here,'../dist/LanBridge-v1.2.0.exe'),['--test-mode','--port',String(port),'--data',root],{windowsHide:true,stdio:'ignore'});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
try {
 let boot;for(let i=0;i<80;i++){const p=path.join(root,'Data/test-bootstrap.json');if(fs.existsSync(p)){boot=JSON.parse(fs.readFileSync(p));break;}await sleep(100);}assert.ok(boot);
 const ipList=Object.values(os.networkInterfaces()).flat().filter(x=>x.family==='IPv4' && !x.internal && /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.|169\.254\.)/.test(x.address));
 const label=Buffer.concat(boot.host.split('.').map(x=>Buffer.concat([Buffer.from([x.length]),Buffer.from(x)])));
 const query=Buffer.concat([Buffer.from([0x71,0x34,0,0,0,1,0,0,0,0,0,0]),label,Buffer.from([0,0,1,0,1])]);
 let success;
 for(const nic of ipList){
  const sock=dgram.createSocket('udp4');
  try {success=await new Promise((resolve,reject)=>{const timeout=setTimeout(()=>reject(new Error('mDNS timeout')),3000);sock.on('error',e=>{clearTimeout(timeout);reject(e);});sock.on('message',b=>{if(b[0]===0x71 && b[1]===0x34 && b[7]===1 && b.includes(Buffer.from(boot.host.split('.')[0]))){clearTimeout(timeout);resolve(b);}});sock.bind(0,nic.address,()=>{sock.setMulticastInterface(nic.address);sock.send(query,5353,'224.0.0.251');});});}catch{}finally{sock.close();}
  if(success)break;
 }
 assert.ok(success,'No local multicast response; check network permissions / firewall');
 console.log('PASS live mDNS query returned stable hostname with IPv4 answer');
 fs.writeFileSync(path.join(here,'discovery-result.json'),JSON.stringify({passed:true,runtime:root,at:new Date().toISOString()},null,2));
}finally{child.kill();}
