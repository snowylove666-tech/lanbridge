import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url));
const root=path.join(here,'runtime-v11-preview'), port=49331;
fs.mkdirSync(root,{recursive:true});
const bootstrap=path.join(root,'Data/test-bootstrap.json');if(fs.existsSync(bootstrap))fs.unlinkSync(bootstrap);
const executable=path.resolve(here,'../dist/LanBridge-v1.1.0.exe');
const child=spawn(executable,['--test-mode','--port',String(port),'--data',root],{windowsHide:true,stdio:'ignore',detached:true});
child.unref();
for(let i=0;i<80;i++) {
 if(fs.existsSync(bootstrap)) { const boot=JSON.parse(fs.readFileSync(bootstrap,'utf8'));const result=await fetch(`http://127.0.0.1:${port}/health`);if(!result.ok)throw new Error('preview health failed');fs.writeFileSync(path.join(root,'process-id.txt'),String(child.pid));console.log(JSON.stringify({port,code:boot.code,pid:child.pid}));process.exit(0); }
 await new Promise(resolve=>setTimeout(resolve,100));
}
throw new Error('preview startup timed out');
