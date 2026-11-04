import {spawn} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url));
const root=fs.mkdtempSync(path.join(here,'runtime-job-')),ready=path.join(root,'ready.txt');
const parent=spawn('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',path.join(here,'job-parent.ps1'),'-Executable',path.resolve(here,'../dist/LanBridge-v1.2.0.exe'),'-ReadyFile',ready],{windowsHide:true,stdio:['ignore','pipe','pipe']});
let output='',childPid; parent.stderr.on('data',b=>output+=b.toString());
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const running=pid=>{try{process.kill(pid,0);return true;}catch{return false;}};
try{
 for(let i=0;i<100;i++){if(fs.existsSync(ready)){childPid=Number(fs.readFileSync(ready));break;}if(parent.exitCode!==null)throw new Error(output);await sleep(100);}
 assert.ok(childPid,'Child process not ready');assert.ok(running(childPid));
 parent.kill();await new Promise(r=>parent.once('exit',r));
 for(let i=0;i<40 && running(childPid);i++)await sleep(100);
 assert.equal(running(childPid),false,'Child survived forced parent termination');
 console.log('PASS Windows Job Object kills module child after forced parent exit');
 fs.writeFileSync(path.join(here,'job-result.json'),JSON.stringify({passed:true,at:new Date().toISOString()},null,2));
}finally{if(parent.exitCode===null)parent.kill();if(childPid && running(childPid))process.kill(childPid);}
