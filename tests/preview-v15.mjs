import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url));
const root=fs.mkdtempSync(path.join(here,'runtime-v15-preview-')),port=49432;
const previewExe=path.join(root,'LanBridge-UI-Preview.exe');fs.copyFileSync(path.resolve(here,'../dist/LanBridge-v1.5.0.exe'),previewExe);
const child=spawn(previewExe,['--test-mode','--show-test-ui','--preview-only','--port',String(port),'--data',root],{windowsHide:false,stdio:'ignore',detached:true});child.unref();
for(let i=0;i<100;i++){
 if(fs.existsSync(path.join(root,'Data/settings.bin'))){fs.writeFileSync(path.join(root,'process-id.txt'),String(child.pid));console.log(JSON.stringify({port,pid:child.pid,root}));process.exit(0);}
 if(child.exitCode!==null || fs.existsSync(path.join(root,'test-error.txt')))throw new Error(fs.existsSync(path.join(root,'test-error.txt'))?fs.readFileSync(path.join(root,'test-error.txt'),'utf8'):'preview stopped');
 await new Promise(r=>setTimeout(r,100));
}throw new Error('preview startup timed out');
