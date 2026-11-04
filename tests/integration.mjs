import {spawn} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';

const here=path.dirname(fileURLToPath(import.meta.url));
const root=fs.mkdtempSync(path.join(here,'runtime-'));
const executable=process.argv[2] ? path.resolve(process.argv[2]) : path.resolve(here,'../dist/LanBridge.exe');
const port=49329, base=`http://127.0.0.1:${port}`;
let processHandle, boot, count=0;
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const check=(name,fn)=>Promise.resolve().then(fn).then(()=>{count++;console.log(`PASS ${name}`);});
async function start() {
 const bootstrap=path.join(root,'Data/test-bootstrap.json');
 if(fs.existsSync(bootstrap)) fs.unlinkSync(bootstrap);
 processHandle=spawn(executable,['--test-mode','--test-remote','--port',String(port),'--data',root],{windowsHide:true,stdio:'ignore'});
 for(let i=0;i<100;i++) { if(fs.existsSync(bootstrap)) { boot=JSON.parse(fs.readFileSync(bootstrap,'utf8')); return; } if(processHandle.exitCode!==null) throw new Error('服务启动失败 '+(fs.existsSync(path.join(root,'test-error.txt'))?fs.readFileSync(path.join(root,'test-error.txt'),'utf8'):'')); await sleep(100); }
 throw new Error('服务启动超时');
}
async function stop() { if(!processHandle || processHandle.exitCode!==null) return; processHandle.kill(); await new Promise(r=>processHandle.once('exit',r)); }
async function request(route,{token,method='GET',body,headers={}}={}) {
 const response=await fetch(base+route,{method,body,headers:{...(token?{Authorization:`Bearer ${token}`} : {}),...headers}});
 const bytes=Buffer.from(await response.arrayBuffer()); let data; try{data=JSON.parse(bytes.toString());}catch{data=bytes.toString();}
 return {status:response.status,data,bytes,headers:response.headers};
}
const json=(route,body,token)=>request(route,{method:'POST',token,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
const control=(action,extra={})=>json('/test/control',{action,...extra},boot.key);
async function pair(code=boot.pair,name='测试 iPhone') {const r=await json('/v1/pair',{code,name});assert.equal(r.status,200);return r.data.token;}
async function raw(text) { return new Promise((resolve,reject)=>{ const socket=net.connect(port,'127.0.0.1'); let data='';socket.setTimeout(5000,()=>{socket.destroy();reject(new Error('socket timeout'));});socket.on('connect',()=>socket.write(text));socket.on('data',b=>data+=b.toString());socket.on('end',()=>resolve(data));socket.on('error',reject);}); }
try {
 await start(); let token;
 await check('健康检查与嵌入式界面',async()=>{assert.equal((await request('/health')).data.app,'LanBridge'); const r=await request('/');assert.equal(r.status,200);assert.match(r.data,/轻桥/);assert.match(r.headers.get('content-security-policy'),/frame-ancestors 'none'/);assert.equal((await request('/app.js')).status,200);});
 await check('未配对无法读取剪贴板和文件',async()=>{assert.equal((await request('/v1/state')).status,401);assert.equal((await request('/v1/clipboard.txt')).status,401);});
 await check('同热点提供稳定名称，局域网端口只接受自己的名称',async()=>{const info=(await request('/v1/connection')).data;assert.equal(info.remote,false);assert.equal(info.localUrl,`http://${boot.host}:${port}`);assert.match(await raw(`GET /health HTTP/1.1\r\nHost: ${boot.host}:${port}\r\n\r\n`),/200 OK/);assert.match(await raw(`GET /health HTTP/1.1\r\nHost: another-qiao.local:${port}\r\n\r\n`),/403/);});
 await check('免扫码请求在电脑允许前无权读取数据',async()=>{const joined=await json('/v1/join',{name:'免扫码 iPhone'});assert.equal(joined.status,200);assert.match(joined.data.check,/^\d{4}$/);assert.equal((await json('/v1/join/poll',{key:joined.data.key})).data.pending,true);assert.equal((await request('/v1/state',{token:joined.data.key})).status,401);await control('approve-join',{key:joined.data.key});const granted=(await json('/v1/join/poll',{key:joined.data.key})).data;assert.equal(granted.pending,false);assert.equal((await request('/v1/state',{token:granted.token})).status,200);assert.equal((await json('/v1/join/poll',{key:joined.data.key})).data.token,granted.token);});
 await check('免扫码拒绝和错误轮询密钥不会获得令牌',async()=>{const joined=(await json('/v1/join',{name:'拒绝测试'})).data;await control('reject-join',{key:joined.key});assert.equal((await json('/v1/join/poll',{key:joined.key})).status,403);assert.equal((await json('/v1/join/poll',{key:'bad'})).status,403);});
 const remoteRaw=wire=>new Promise((resolve,reject)=>{const s=net.connect(boot.remotePort,'127.0.0.1',()=>s.write(wire));let data='';s.setTimeout(3000,()=>s.destroy());s.on('data',b=>data+=b.toString());s.on('end',()=>resolve(data));s.on('error',reject);});
 const remoteJson=(route,body,origin='https://test-only.invalid')=>{const data=JSON.stringify(body);return remoteRaw(`POST ${route} HTTP/1.1\r\nHost: test-only.invalid\r\nOrigin: ${origin}\r\nContent-Type: application/json\r\nContent-Length: ${Buffer.byteLength(data)}\r\n\r\n${data}`);};
 await check('跨网独立入口只接受本次 HTTPS 主机和同源请求',async()=>{assert.match(await remoteRaw('GET /v1/connection HTTP/1.1\r\nHost: test-only.invalid\r\n\r\n'),/"remote":true/);assert.match(await remoteRaw(`GET /health HTTP/1.1\r\nHost: 127.0.0.1:${boot.remotePort}\r\n\r\n`),/403/);assert.match(await remoteJson('/v1/pair',{code:boot.pair},'https://evil.example'),/403/);assert.match(await raw(`GET /health HTTP/1.1\r\nHost: test-only.invalid\r\n\r\n`),/403/);});
 let remoteToken;
 await check('跨网禁止六位码和免码申请，只接受随机二维码密钥',async()=>{assert.match(await remoteJson('/v1/join',{name:'remote'}),/403/);assert.match(await remoteJson('/v1/pair',{code:boot.code}),/403/);const r=await remoteJson('/v1/pair',{code:boot.pair,name:'跨网 iPhone'});assert.match(r,/200 OK/);assert.match(r,/"token":"[a-f0-9]{64}"/);remoteToken=JSON.parse(r.split('\r\n\r\n')[1]).token;await control('refresh');boot.pair=(await control('refresh')).data.pair;});
 await check('跨网关闭后撤销临时凭据并拒绝访问，局域网仍正常',async()=>{await control('stop-remote');assert.match(await remoteRaw('GET /health HTTP/1.1\r\nHost: test-only.invalid\r\n\r\n'),/403/);assert.equal((await request('/v1/state',{token:remoteToken})).status,401);assert.equal((await request('/health')).status,200);});
 await check('拒绝错误配对码',async()=>assert.equal((await json('/v1/pair',{code:'wrong'})).status,403));
 await check('拒绝跨域配对与 DNS 重绑定',async()=>{assert.equal((await request('/v1/pair',{method:'POST',headers:{Origin:'https://evil.example','Content-Type':'application/json'},body:JSON.stringify({code:boot.pair})})).status,403);const r=await raw(`GET /health HTTP/1.1\r\nHost: evil.example:${port}\r\n\r\n`);assert.match(r,/403/);});
 await check('二维码密钥可配对',async()=>{token=await pair();});
 await check('配对码使用后立即失效',async()=>assert.equal((await json('/v1/pair',{code:boot.pair})).status,403));
 await check('配对密钥经 Windows 用户加密保存',async()=>{const bytes=fs.readFileSync(path.join(root,'Data/settings.bin'));assert.ok(!bytes.includes(Buffer.from('测试 iPhone')));assert.ok(!bytes.includes(Buffer.from(token)));});
 await check('空剪贴板不会覆盖手机原内容',async()=>assert.equal((await request('/v1/clipboard.txt',{token})).status,404));
 const text='轻桥测试 → Windows / iPhone\n多行文字 😀 <script>alert(1)</script>\n"quotes" & + /';
 await check('手机发送 Unicode、多行文字进入接收回调',async()=>{assert.equal((await json('/v1/clipboard',{text},token)).status,200);assert.equal(fs.readFileSync(path.join(root,'Data/test-received-clipboard.txt'),'utf8'),text);});
 await check('快捷指令接收接口返回纯文本原文',async()=>{const r=await request('/v1/clipboard.txt',{token});assert.equal(r.data,text);assert.match(r.headers.get('content-type'),/text\/plain/);});
 await check('空文字和非 JSON 请求被拒绝',async()=>{assert.equal((await json('/v1/clipboard',{text:''},token)).status,400);assert.equal((await request('/v1/clipboard',{method:'POST',token,body:'plain text'})).status,415);});
 await check('电脑发送更新共享剪贴板',async()=>{assert.equal((await control('publish',{text:'来自电脑的文字'})).status,200);assert.equal((await request('/v1/state',{token})).data.clipboard.source,'Windows');});
 await check('文字十分钟过期',async()=>{await control('expire');assert.equal((await request('/v1/clipboard.txt',{token})).status,404);assert.equal((await request('/v1/state',{token})).data.clipboard.text,'');});
 await check('限制超过 1 MB 的文字请求',async()=>{const r=await json('/v1/clipboard',{text:'a'.repeat(1100001)},token);assert.equal(r.status,413);});
 let file;
 const fileBytes=crypto.randomBytes(256*1024);
 await check('手机文件上传与内容完整性',async()=>{const r=await request('/v1/files?name='+encodeURIComponent('测试照片 😀.bin'),{method:'POST',token,headers:{'Content-Type':'application/octet-stream'},body:fileBytes});assert.equal(r.status,200);file=r.data;const stored=fs.readdirSync(path.join(root,'Received')).find(n=>n.startsWith(file.id));assert.deepEqual(fs.readFileSync(path.join(root,'Received',stored)),fileBytes);});
 await check('文件名路径穿越与 Windows 保留名被规范化',async()=>{const r=await request('/v1/files?name='+encodeURIComponent('../../CON.txt'),{method:'POST',token,body:'safe'});assert.equal(r.status,200);assert.equal(r.data.name,'_CON.txt');assert.ok(!fs.existsSync(path.join(root,'CON.txt')));});
 await check('相同文件名互不覆盖',async()=>{for(let i=0;i<2;i++) assert.equal((await request('/v1/files?name=same.txt',{method:'POST',token,body:String(i)})).status,200);const r=await request('/v1/state',{token});assert.equal(r.data.files.filter(x=>x.name==='same.txt').length,2);});
 await check('零字节文件也可传输',async()=>assert.equal((await request('/v1/files?name=empty.txt',{method:'POST',token,body:''})).data.size,0));
 await check('认证前禁止下载文件',async()=>assert.equal((await request('/v1/download/'+file.id)).status,403));
 let ticket;
 await check('授权下载保持文件原始字节与中文名称',async()=>{ticket=(await request('/v1/tickets/'+file.id,{method:'POST',token})).data.url;const r=await request(ticket);assert.equal(r.status,200);assert.deepEqual(r.bytes,fileBytes);assert.match(r.headers.get('content-disposition'),/filename\*=UTF-8/);});
 await check('下载票据只能使用一次',async()=>assert.equal((await request(ticket)).status,403));
 await check('不存在的文件无法申请票据',async()=>assert.equal((await request('/v1/tickets/nope',{method:'POST',token})).status,404));
 await check('上传中断清理半成品',async()=>{await raw(`POST /v1/files?name=truncated.bin HTTP/1.1\r\nHost: 127.0.0.1:${port}\r\nAuthorization: Bearer ${token}\r\nContent-Length: 100\r\n\r\nx`).catch(()=>{}); // Socket timeout closes the sender; server cleans on EOF.
 await sleep(200);assert.equal(fs.readdirSync(path.join(root,'Received')).filter(n=>n.endsWith('.part')).length,0);});
 await check('请求解析拒绝重复 Content-Length 和分块歧义',async()=>{assert.match(await raw(`POST /v1/files HTTP/1.1\r\nHost: 127.0.0.1:${port}\r\nContent-Length: 0\r\nContent-Length: 1\r\n\r\n`),/400/);assert.match(await raw(`POST /v1/files HTTP/1.1\r\nHost: 127.0.0.1:${port}\r\nTransfer-Encoding: chunked\r\n\r\n`),/400/);});
 await check('大于 1 GB 的上传在读取前拒绝',async()=>assert.match(await raw(`POST /v1/files HTTP/1.1\r\nHost: 127.0.0.1:${port}\r\nContent-Length: 1073741825\r\n\r\n`),/413/));
 await check('暂停阻止剪贴板、上传及已签发下载票据',async()=>{const pending=(await request('/v1/tickets/'+file.id,{method:'POST',token})).data.url;await control('pause');assert.equal((await request('/v1/state',{token})).status,503);assert.equal((await json('/v1/clipboard',{text:'paused'},token)).status,503);assert.equal((await request(pending)).status,503);await control('resume');});
 const big=crypto.randomBytes(16*1024*1024), bigPath=path.join(root,'large-test.bin');fs.writeFileSync(bigPath,big);
 await check('电脑到手机 16 MB 流式传输校验',async()=>{assert.equal((await control('add-file',{path:bigPath})).status,200);const state=(await request('/v1/state',{token})).data;const out=state.files.find(f=>f.direction==='toPhone');assert.ok(out);const url=(await request('/v1/tickets/'+out.id,{method:'POST',token})).data.url;const got=(await request(url)).bytes;assert.equal(crypto.createHash('sha256').update(got).digest('hex'),crypto.createHash('sha256').update(big).digest('hex'));});
 const sync=(text,mode='smart')=>json('/v1/sync',{text,mode},token);
 await check('智能同步首次只初始化，不发送旧手机内容',async()=>{token=await pair((await control('refresh')).data.pair,'智能同步 iPhone');const before=fs.readFileSync(path.join(root,'Data/test-received-clipboard.txt'),'utf8');assert.equal((await sync('手机原有旧文字')).data.action,'ready');assert.equal(fs.readFileSync(path.join(root,'Data/test-received-clipboard.txt'),'utf8'),before);});
 let offered;
 await check('同一智能入口自动判断电脑到手机',async()=>{await control('publish',{text:'新的电脑文字 A'});const r=await sync('手机原有旧文字');assert.equal(r.status,200);assert.equal(r.data.action,'receive');assert.equal(r.data.copy,true);assert.equal(r.data.text,'新的电脑文字 A');offered=r.data;});
 await check('接收未确认可重取，确认后不会重复回灌',async()=>{const retry=(await sync('手机原有旧文字')).data;assert.equal(retry.receipt,offered.receipt);assert.equal((await json('/v1/ack',{receipt:offered.receipt},token)).status,200);assert.equal((await sync('新的电脑文字 A')).data.action,'noop');});
 await check('接收确认可安全重试',async()=>assert.equal((await json('/v1/ack',{receipt:offered.receipt},token)).status,200));
 await check('同一智能入口自动判断手机到电脑且无循环',async()=>{const r=(await sync('手机新复制的文字 B')).data;assert.equal(r.action,'sent');assert.equal(r.copy,false);assert.equal(fs.readFileSync(path.join(root,'Data/test-received-clipboard.txt'),'utf8'),'手机新复制的文字 B');assert.equal((await sync('手机新复制的文字 B')).data.action,'noop');});
 await check('两端同时变化自动采用本次观察到的手机文字，不再询问',async()=>{await control('publish',{text:'电脑较早观察内容 C'});const r=(await sync('手机本次观察内容 D')).data;assert.equal(r.action,'sent');assert.equal(r.copy,false);assert.equal(fs.readFileSync(path.join(root,'Data/test-received-clipboard.txt'),'utf8'),'手机本次观察内容 D');assert.equal((await request('/v1/state',{token})).data.clipboard.text,'手机本次观察内容 D');assert.equal((await sync('手机本次观察内容 D')).data.action,'noop');});
 await check('显式接收可恢复当前最新内容，再继续智能同步',async()=>{const r=(await sync('other','receive')).data;assert.equal(r.text,'手机本次观察内容 D');assert.equal((await json('/v1/ack',{receipt:r.receipt},token)).status,200);assert.equal((await sync('手机本次观察内容 D')).data.action,'noop');});
 await check('手机新文字覆盖旧电脑内容，不再回灌旧副本',async()=>{await control('publish',{text:'电脑旧副本 E'});assert.equal((await sync('手机指定发送 F','send')).data.action,'sent');const r=(await sync('手机指定发送 F','receive')).data;assert.equal(r.text,'手机指定发送 F');await json('/v1/ack',{receipt:r.receipt},token);});
 await check('两分钟以前的电脑文字不被智能模式回灌',async()=>{await control('publish',{text:'三分钟前的电脑文字 G'});await control('age-pc');assert.equal((await sync('手机指定发送 F')).data.action,'noop');const r=(await sync('手机指定发送 F','receive')).data;assert.equal(r.text,'三分钟前的电脑文字 G');await json('/v1/ack',{receipt:r.receipt},token);});
 await check('确认丢失后，通过相同文字恢复同步基线',async()=>{await control('publish',{text:'已经复制但确认丢失 H'});assert.equal((await sync('三分钟前的电脑文字 G')).data.action,'receive');assert.equal((await sync('已经复制但确认丢失 H')).data.action,'noop');assert.equal((await sync('手机后续新文字 I')).data.action,'sent');});
 await check('错误确认和无效模式被拒绝',async()=>{assert.equal((await json('/v1/ack',{receipt:'wrong'},token)).status,409);assert.equal((await sync('x','invalid')).status,400);});
 await check('空手机剪贴板和暂停不会误覆盖电脑',async()=>{const before=fs.readFileSync(path.join(root,'Data/test-received-clipboard.txt'),'utf8');assert.equal((await sync('','send')).data.action,'noop');assert.equal(fs.readFileSync(path.join(root,'Data/test-received-clipboard.txt'),'utf8'),before);await control('pause');assert.equal((await sync('x')).status,503);await control('resume');});
 await check('资源管理器发送命令进入已有实例，不需打开窗口',async()=>{const p=path.join(root,'from-explorer.txt');fs.writeFileSync(p,'Explorer test data');const cmd=spawn(executable,['--test-mode','--port',String(port),'--send',p],{windowsHide:true,stdio:'ignore'});await new Promise((resolve,reject)=>{cmd.once('error',reject);cmd.once('exit',code=>code===0?resolve():reject(new Error('send CLI failed')));});let found=false;for(let i=0;i<30;i++){const r=await request('/v1/state',{token});found=r.data.files.some(f=>f.name==='from-explorer.txt' && f.direction==='toPhone');if(found)break;await sleep(100);}assert.ok(found);});
 await check('服务重启保留配对和文件，不保留共享文字',async()=>{await stop();await start();const r=await request('/v1/state',{token});assert.equal(r.status,200);assert.ok(r.data.files.length>=6);assert.equal(r.data.clipboard.text,'');});
 await check('服务重启保留智能基线，不上传旧文字',async()=>assert.equal((await sync('手机后续新文字 I')).data.action,'noop'));
 await check('配对码连续错误触发限流',async()=>{for(let i=0;i<5;i++) assert.equal((await json('/v1/pair',{code:'bad'})).status,403);assert.equal((await json('/v1/pair',{code:boot.pair})).status,429);});
 await check('撤销设备使令牌和旧下载链接失效',async()=>{const pending=(await request('/v1/tickets/'+file.id,{method:'POST',token})).data.url;await control('revoke');assert.equal((await request('/v1/state',{token})).status,401);assert.equal((await request(pending)).status,401);});
 console.log(`\n${count} integration checks passed. Runtime: ${root}`);
 fs.writeFileSync(path.join(here,'last-result.json'),JSON.stringify({checks:count,passed:true,runtime:root,at:new Date().toISOString()},null,2));
} finally { await stop(); }
