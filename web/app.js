'use strict';
const $ = id => document.getElementById(id);
const storageKey = 'lanbridge.device.v1';
let token = '', pairing = '', snapshot = null, busy = false, pollTimer, toastTimer, activeUpload = null, stopUploads = false;
let connection = {remote:location.protocol==='https:',maxFile:1073741824}, joinTimer;
let session=0,pairAttempt=0,cancelJoinWait=null,uploading=false,reachable=false,sending=false,syncing=false,copying=false;
const requests=new Set();
const canReadClipboard=()=>!!(window.isSecureContext && navigator.clipboard && typeof navigator.clipboard.readText==='function');
const canceled=()=>Object.assign(new Error('操作已取消'),{silent:true});
function feedback(id,message,kind='') { const element=$(id); element.textContent=message; element.hidden=!message; element.className='feedback'+(kind?' '+kind:''); }
function errorText(error) { return error.name==='NotAllowedError'?'浏览器未允许剪贴板访问。请长按文字复制、在输入框长按粘贴，或运行背面指令。':error.message; }
try { token = localStorage.getItem(storageKey) || ''; } catch {}
const hash = new URLSearchParams(location.hash.slice(1));
if (hash.has('pair')) { pairing = hash.get('pair'); history.replaceState(null, '', location.pathname); }
if (pairing) { $('pin-field').hidden = true; $('qr-hint').hidden = false; }
async function configureConnection() {
 $('quick-sync-card').hidden=!canReadClipboard(); $('paste-text').hidden=!canReadClipboard();
 try { connection=await api('/v1/connection'); } catch(error) { feedback('pair-error',errorText(error),'warning'); }
 const remote=connection.remote;
 $('pair-computer').textContent=connection.computer?'连接 '+connection.computer:'连接这台电脑';
 if(connection.computer)$('computer-name').textContent=connection.computer;
 $('connection-address').textContent=location.origin;
 $('quick-sync-card').hidden=!canReadClipboard();
 $('paste-text').hidden=!canReadClipboard();
 $('local-join-hint').hidden=remote || !!pairing;
 if(remote) {
  $('pin-field').hidden=true;
  $('pair-network-hint').textContent='临时跨网最多持续 30 分钟。请使用电脑本次生成的完整链接或二维码，到期后需获取新链接。';
  $('network-note').textContent='◉ HTTPS · 流量经 Cloudflare，非端到端加密。';
  $('network-footer').textContent='轻桥 1.5 · 临时跨网 · 经 Cloudflare · 非端到端加密';
  $('clip-guidance').textContent='跨网可在此手动收发文字。原背面指令仍使用局域网地址，连接本页不会自动修改它。';
  $('remote-guide-note').hidden=false;
  if(!pairing && !token) { feedback('pair-error','请在电脑开启临时跨网后，打开本次完整配对链接或扫描当前二维码。','warning'); $('pair-submit').disabled=true; }
 }
 if(!canReadClipboard()) $('clip-guidance').textContent=remote?'此浏览器无法直接读取剪贴板，请用下方复制按钮和粘贴框。原背面指令仍使用局域网地址。':'日常可运行“轻桥同步”背面指令。网页请用下方复制按钮和粘贴框；首次配置在“设置与帮助”。';
 const uploadHint=$('drop-zone').querySelector('p'); if(uploadHint) uploadHint.textContent=remote?'临时跨网单文件最大 90 MB。大文件建议同热点直传。':'照片、视频、文档，都可以。每个文件最大 1 GB。';
}

function toast(message) { $('toast').textContent = message; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => { $('toast').hidden = true; }, 4500); }
function storeToken(value) { session++; token=value; snapshot=null; clearTimeout(pollTimer); requests.forEach(r=>r.abort()); requests.clear(); try { if(value) localStorage.setItem(storageKey,value); else localStorage.removeItem(storageKey); } catch { if(value) toast('浏览器未允许保存配对信息，关闭后需要重新配对。'); } }
async function api(path, options = {}) {
 const controller=new AbortController(),generation=session,credential=token; const timer=setTimeout(()=>controller.abort(),15000); requests.add(controller);
 try {
  const response=await fetch(path,{...options,cache:'no-store',signal:controller.signal,headers:{...(credential?{Authorization:`Bearer ${credential}`} : {}),...options.headers}});
  let data; try { data=await response.json(); } catch { throw Object.assign(new Error(`电脑或跨网通道未返回有效结果 (${response.status})，请检查电脑上的通道状态。`),{status:response.status,uncertain:!!options.method}); }
  if(generation!==session) throw canceled();
  if (!response.ok) { const e = new Error(data.error || `连接失败 (${response.status})`); e.status = response.status; throw e; }
  return data;
 } catch(e) { if(generation!==session) throw canceled(); if(e.status || e.silent || e.uncertain) throw e; throw Object.assign(new Error(e.name==='AbortError'?'连接超时。请确认电脑开机、轻桥正在运行，并检查网络。':'无法联系电脑。请检查网络；临时跨网地址到期后需获取新链接。'),{uncertain:!!options.method}); }
 finally { clearTimeout(timer); requests.delete(controller); }
}
function connectedLabel() { return connection.remote?'已连接 · 临时跨网':'已连接 · 本地直传'; }
function showPaired() { $('pair-screen').hidden=true; $('app-screen').hidden=false; }
function showPair() { $('pair-screen').hidden=false; $('app-screen').hidden=true; $('connection').classList.add('offline'); $('connection').querySelector('span').textContent='等待连接'; }
function online(value,error) { reachable=value; $('offline-banner').hidden=value; $('file-picker').disabled=!value || uploading; $('paste-files').disabled=!value || uploading; $('send-text-button').disabled=!value || sending; $('quick-sync').disabled=!value || syncing; $('outbox-wait').hidden=value; if(!value) { $('outbox-empty').hidden=true; $('outbox-wait').textContent=snapshot?'当前显示的是先前文件记录，恢复连接后会自动更新。':'等待连接电脑后更新文件列表。'; } $('connection').classList.toggle('offline',!value); $('connection').querySelector('span').textContent=value?connectedLabel():error && error.status===503?'电脑互传已暂停':'正在重连'; if(!value) $('connection-error').textContent=error && error.status===503?'电脑已暂停互传，请在电脑的设置中恢复。':errorText(error || new Error('正在检查电脑连接…')); }
function bytes(n) { return n < 1024 ? `${n} B` : n < 1048576 ? `${(n/1024).toFixed(1)} KB` : n < 1073741824 ? `${(n/1048576).toFixed(1)} MB` : `${(n/1073741824).toFixed(2)} GB`; }
function timeLabel(value) { const date = new Date(value); return Number.isNaN(date.getTime()) ? '' : date.toLocaleString('zh-CN', {month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit'}); }
function fileRow(file, canDownload) {
 const entry=document.createElement('div'); entry.className='file-entry';
 const row = document.createElement('div'); row.className = 'file-row';
 const icon = document.createElement('span'); icon.className = 'file-icon'; icon.textContent = (file.name.split('.').pop() || 'FILE').slice(0,4).toUpperCase();
 const info = document.createElement('div'); info.className = 'file-info';
 const name = document.createElement('div'); name.className = 'file-name'; name.textContent = file.name; name.title = file.name;
 const meta = document.createElement('small'); meta.textContent = `${bytes(file.size)} · ${timeLabel(file.time)}`; info.append(name,meta); row.append(icon,info);
 if (canDownload) {
  const button = document.createElement('button'); button.className = 'download-button'; button.textContent = '下载'; button.setAttribute('aria-label', `下载 ${file.name}`);
  const result=document.createElement('p'); result.className='download-status'; result.setAttribute('role','status'); result.hidden=true;
  button.addEventListener('click',async()=> { const generation=session; button.disabled=true; result.hidden=false; result.textContent='正在申请下载…'; try { const ticket=await api(`/v1/tickets/${encodeURIComponent(file.id)}`,{method:'POST'}); if(generation!==session) return; if(!/^\/v1\/download\/[a-f0-9]+\?ticket=[a-f0-9]+$/.test(ticket.url || '')) throw new Error('下载地址无效，请重新连接电脑。'); const anchor=document.createElement('a'); anchor.href=ticket.url; anchor.download=file.name; document.body.append(anchor); anchor.click(); anchor.remove(); result.textContent='已请求浏览器下载。请在下载列表确认保存完成；未开始可再次点击下载。'; } catch(error) { if(!error.silent && generation===session) result.textContent='下载未开始：'+errorText(error); } finally { button.disabled=false; } }); row.append(button); entry.append(row,result);
 } else { const check=document.createElement('span'); check.className='sent-check'; check.textContent='已保存到电脑'; row.append(check); entry.append(row); }
 return entry;
}
function render(data) {
 const previous = snapshot; snapshot = data;
 $('computer-name').textContent = data.computer; $('my-name').textContent = data.name;
 $('setup-status').textContent=`${data.autoClipboard?'电脑已开启新复制文字自动共享':'电脑自动共享已关闭，请到电脑“文字”页开启'}。${data.syncInitialized?'智能指令已初始化。':'首次运行“轻桥同步”只建立同步基线。'}`;
 const clip = data.clipboard;
 if (!previous || previous.clipboard.text !== clip.text || previous.clipboard.time !== clip.time) {
  $('clip-preview').textContent = clip.text || '暂无共享文字'; $('copy-fallback').hidden = true;
  feedback('copy-result','');
 }
 $('clip-source').textContent=clip.text?`来自 ${clip.source} · ${timeLabel(clip.time)}${Date.now()-new Date(clip.time).getTime()>120000?' · 智能指令接收已超时，请在电脑重新复制；此处仍可手动复制。':''}`:'在电脑开启自动共享，然后重新复制文字。';
 $('copy-clip').disabled=copying || !clip.text; $('clip-age').textContent=clip.text?`${Math.ceil(clip.expiresIn/60)} 分钟后过期`:'';
 if (!previous || JSON.stringify(previous.files) !== JSON.stringify(data.files)) {
  const outgoing = data.files.filter(f => f.direction === 'toPhone'), incoming = data.files.filter(f => f.direction === 'toPC');
  $('outbox-list').replaceChildren(...outgoing.map(f => fileRow(f,true))); $('sent-list').replaceChildren(...incoming.map(f=>fileRow(f,false)));
 $('outbox-count').textContent=outgoing.length; $('sent-count').textContent=incoming.length; $('outbox-empty').hidden=outgoing.length>0; $('sent-empty').hidden=incoming.length>0;
 }
}
async function poll() {
 clearTimeout(pollTimer); if (!token || busy || document.hidden) return;
 const generation=session; busy=true;
 try { const data=await api('/v1/state'); if(generation!==session) return; showPaired(); online(true); render(data); }
 catch(e) { if(e.silent || generation!==session) return; if(e.status===401) { storeToken(''); $('auth-value').textContent=''; showPair(); feedback('pair-error',connection.remote?'跨网配对已失效，请从电脑获取新的完整链接或二维码。':'原配对已失效，点连接后在电脑核对识别号允许即可。','error'); $('pair-submit').disabled=!!connection.remote; } else { showPaired(); online(false,e); } }
 finally { busy = false; if(token) pollTimer = setTimeout(poll, 2200); }
}
function waitForJoin(attempt) { return new Promise((resolve,reject)=>{ joinTimer=setTimeout(()=>{cancelJoinWait=null; if(attempt!==pairAttempt) reject(canceled()); else resolve();},1500); cancelJoinWait=()=>{clearTimeout(joinTimer);cancelJoinWait=null;reject(canceled());}; }); }
function resetPair() { $('pair-submit').disabled=!!connection.remote && !pairing && !token; $('pair-submit').textContent='连接电脑'; $('join-wait').hidden=true; $('cancel-join').hidden=true; }
$('pair-form').addEventListener('submit', async e => {
 e.preventDefault(); const attempt=++pairAttempt,button=$('pair-submit'); button.disabled=true; $('pair-error').hidden=true;
 try {
  if(!pairing && !$('pair-code').value && !connection.remote) {
   const result=await api('/v1/join',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:$('device-name').value})});
   if(attempt!==pairAttempt) throw canceled();
   $('join-wait').textContent=`请在电脑核对识别号 ${result.check}，再允许本次设备连接。等待最多两分钟。`; $('join-wait').hidden=false; $('cancel-join').hidden=false;
   button.textContent='等待电脑允许…'; button.disabled=true;
   const deadline=Date.now()+120000; let approved;
   do { await waitForJoin(attempt); approved=await api('/v1/join/poll',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({key:result.key})}); if(attempt!==pairAttempt) throw canceled(); if(!approved.pending) break; } while(Date.now()<deadline);
   if(approved.pending) throw new Error('等待允许已超时，请在电脑打开轻桥后重新连接。');
   await completePair(approved); return;
  }
  const result = await api('/v1/pair', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({code:pairing || $('pair-code').value, name:$('device-name').value})});
  if(attempt!==pairAttempt) throw canceled(); await completePair(result);
 } catch(error) { if(!error.silent && attempt===pairAttempt) { feedback('pair-error',errorText(error)+(pairing?' 请从电脑刷新配对链接后重新打开。':''),'error'); if(pairing) { pairing=''; $('pin-field').hidden=!!connection.remote; $('qr-hint').hidden=true; } } }
 finally { if(attempt===pairAttempt) resetPair(); }
});
$('cancel-join').addEventListener('click',()=>{pairAttempt++;if(cancelJoinWait)cancelJoinWait();resetPair();feedback('pair-error','已取消本页等待。电脑上的原申请会自动过期，可重新连接。','warning');});
async function completePair(result) { if(!/^[a-f0-9]{64}$/.test(result.token || '')) throw new Error('电脑返回的配对信息无效，请重新连接。'); storeToken(result.token); pairing=''; $('pin-field').hidden=!!connection.remote; $('qr-hint').hidden=true; $('pair-code').value=''; showPaired(); selectTab('files'); setupGuide(); await poll(); }
function selectTab(name) { document.querySelectorAll('[data-tab]').forEach(tab => { const active = tab.dataset.tab===name; tab.classList.toggle('active',active); tab.setAttribute('aria-pressed',String(active)); $('pane-'+tab.dataset.tab).hidden=!active; }); if(name==='guide') setupGuide(); }
document.querySelectorAll('[data-tab]').forEach(button => button.addEventListener('click', () => selectTab(button.dataset.tab)));
$('retry').addEventListener('click',()=>{ $('connection-error').textContent='正在重新检查连接…'; poll(); });
document.addEventListener('visibilitychange', () => { if(document.hidden) clearTimeout(pollTimer); else poll(); });

async function copyText(text) {
 if(navigator.clipboard && window.isSecureContext) { try { await navigator.clipboard.writeText(text); return true; } catch {} }
 const previous = document.activeElement, scroll = window.scrollY;
 const area = document.createElement('textarea'); area.value=text; area.readOnly=true; area.className='copy-helper'; area.setAttribute('aria-label','要复制的文字'); document.body.append(area);
 area.focus({preventScroll:true}); area.select(); area.setSelectionRange(0,area.value.length);
 let success=false; try { success=document.execCommand('copy'); } catch {}
 area.remove(); if(previous && previous.focus) previous.focus({preventScroll:true}); window.scrollTo(0,scroll); return success;
}
$('quick-sync').addEventListener('click',()=> {
 if(syncing || !reachable)return; syncing=true; const button=$('quick-sync'),generation=session; button.disabled=true; feedback('quick-sync-result','正在检查剪贴板并与电脑同步…');
 // Start clipboard access in the original user activation. Safari can reject
 // access; failed reads must never be submitted as an empty phone clipboard.
 const read=canReadClipboard()?navigator.clipboard.readText():Promise.reject(new Error('此浏览器不能直接读取剪贴板，请使用背面指令或下方粘贴框。'));
 const result=read.then(text=>{if(generation!==session)throw canceled();return api('/v1/sync',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({text,mode:'smart'})});});
 // Passing a pending Blob to ClipboardItem preserves user activation on Safari.
 const shouldWrite=result.then(r=> { if(generation!==session)throw canceled();if(!r.copy) throw new Error('NO_COPY'); return r; });
 let writing=null;
 if(navigator.clipboard && typeof navigator.clipboard.write==='function' && window.isSecureContext && window.ClipboardItem) {
  const textBlob=shouldWrite.then(r=>new Blob([r.text],{type:'text/plain'}));
  textBlob.catch(()=>{});
  try { writing=navigator.clipboard.write([new ClipboardItem({'text/plain':textBlob})]); writing.catch(()=>{}); } catch { writing=null; }
 }
 shouldWrite.catch(()=>{});
 result.then(async r=> {
  if(generation!==session) return;
  if(r.copy) {
   if(writing) await writing; else if(!await copyText(r.text)) throw new Error('请在共享剪贴板预览长按复制。');
   if(generation!==session) return;
   feedback('quick-sync-result','已复制到本机，可以粘贴。正在确认同步记录…','success');
   try { await api('/v1/ack',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({receipt:r.receipt})}); if(generation===session) feedback('quick-sync-result','已复制到本机，可以粘贴。同步记录已确认。','success'); }
   catch(error) { if(!error.silent && generation===session) feedback('quick-sync-result','文字已复制到本机，可以粘贴；电脑暂未确认同步记录，恢复连接后再同步即可。','warning'); }
  } else if(writing) { try { await writing; } catch {} }
  if(!r.copy && generation===session) feedback('quick-sync-result',r.message || '当前没有新文字。'); poll();
 }).catch(error=> { if(!error.silent && generation===session) feedback('quick-sync-result',errorText(error),'error'); }).finally(()=> { syncing=false;button.disabled=!reachable; });
});
$('copy-clip').addEventListener('click',async()=> { const text=snapshot && snapshot.clipboard.text,generation=session; if(!text || copying)return;copying=true; const button=$('copy-clip'); button.disabled=true; const ok=await copyText(text); if(generation===session) { $('copy-fallback').hidden=ok; feedback('copy-result',ok?'已复制到本机，可以到其他 App 粘贴。':'浏览器未完成复制，请长按上方文字，选择“全选 → 复制”。',ok?'success':'warning'); } copying=false;button.disabled=!(snapshot && snapshot.clipboard.text); });
$('paste-text').addEventListener('click',async()=> { const generation=session; if(canReadClipboard()) try { const text=await navigator.clipboard.readText(); if(generation===session)$('send-text').value=text; return; } catch {} if(generation===session) { $('send-text').focus(); feedback('send-result','请在输入框内长按，选择“粘贴”。'); } });
$('send-text-form').addEventListener('submit', async e => {
 e.preventDefault(); const text=$('send-text').value,generation=session; if(!text || sending || !reachable)return;sending=true; const button=$('send-text-button'); button.disabled=true; feedback('send-result','正在等待电脑确认…');
 try { await api('/v1/clipboard',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({text})}); if(generation!==session) return; if($('send-text').value===text) $('send-text').value=''; feedback('send-result','电脑已确认写入剪贴板，可直接 Ctrl + V。','success'); poll(); }
 catch(error) { if(!error.silent && generation===session) feedback('send-result',error.uncertain?'尚未收到电脑确认，文字可能已经送达。请先在电脑检查后重试。':errorText(error),'error'); } finally { sending=false;button.disabled=!reachable; }
});
function uploadOne(file,index,count,generation) {
 return new Promise((resolve,reject) => {
  const extension=({'image/png':'.png','image/jpeg':'.jpg','image/gif':'.gif','image/webp':'.webp'})[file.type] || '.bin';
  const name=file.name || `粘贴附件-${Date.now()}-${index+1}${extension}`;
  const xhr=new XMLHttpRequest(); activeUpload=xhr; let inactivity;
  const watch=()=>{clearTimeout(inactivity);inactivity=setTimeout(()=>xhr.abort(),60000);};
  const finish=()=>{clearTimeout(inactivity);if(activeUpload===xhr)activeUpload=null;};
  xhr.open('POST',`/v1/files?name=${encodeURIComponent(name)}`); xhr.setRequestHeader('Authorization',`Bearer ${token}`); xhr.setRequestHeader('Content-Type','application/octet-stream');
  $('progress-name').textContent=`${index+1}/${count} · ${name}`; $('progress-bar').value=0;
  xhr.upload.onprogress=e=> { watch(); if(generation!==session)return; if(e.lengthComputable) { $('progress-bar').value=e.loaded/e.total*100; $('progress-detail').textContent=e.loaded===e.total?'已上传，等待电脑保存并确认…':`${bytes(e.loaded)} / ${bytes(e.total)} · ${Math.round(e.loaded/e.total*100)}%`; } };
  xhr.onload=()=> { finish(); if(generation!==session) {reject(canceled());return;} let data; try {data=JSON.parse(xhr.responseText);}catch{} if(xhr.status>=200 && xhr.status<300 && data && data.id)resolve();else reject(Object.assign(new Error(data && data.error || '电脑未返回保存确认。'),{uncertain:xhr.status>=200 && xhr.status<300})); };
  xhr.onerror=()=> { finish(); reject(Object.assign(new Error('连接中断，保存结果未确认。'),{uncertain:true})); };
  xhr.onabort=()=> { finish(); reject(Object.assign(new Error(stopUploads?'已取消后续发送，当前文件保存结果未确认。':'传输长时间没有响应，保存结果未确认。'),{uncertain:true})); };
  watch(); try {xhr.send(file);}catch(error){finish();reject(error);}
 });
}
async function uploadFiles(files) {
 if(uploading || !files.length) return;
 if(!reachable) {feedback('upload-result','电脑尚未连接，请先恢复连接再发送。','warning');return;}
 if(files.length>32) {feedback('upload-result','一次最多发送 32 个文件，请分批发送。','warning');return;}
 if(files.some(f=>f.size>connection.maxFile)) { feedback('upload-result',connection.remote?'临时跨网单文件最大 90 MB，大文件请同热点直传。':'单个文件最大 1 GB，请先压缩或分卷。','error'); return; }
 const generation=session; uploading=true; stopUploads=false; $('upload-progress').hidden=false; $('file-picker').disabled=true; $('paste-files').disabled=true; $('drop-zone').classList.add('uploading'); feedback('upload-result',''); let done=0;
 try { for(let i=0;i<files.length;i++) { if(stopUploads || generation!==session) break; await uploadOne(files[i],i,files.length,generation); done++; poll(); } if(generation===session) feedback('upload-result',`${done} 个文件已确认保存到电脑 Received 文件夹。${stopUploads?'后续文件未发送。':''}`,'success'); }
 catch(error) { if(!error.silent && generation===session) feedback('upload-result',`${errorText(error)}${done?` 已确认保存 ${done} 个文件。`:''}${error.uncertain?' 请先查看下方接收记录或电脑 Received 文件夹，再决定是否重发。':''}`,'warning'); }
 finally { activeUpload=null; uploading=false; $('file-picker').disabled=!reachable; $('paste-files').disabled=!reachable; $('file-picker').value=''; $('upload-progress').hidden=true; $('drop-zone').classList.remove('uploading'); poll(); }
}
$('file-picker').addEventListener('change',e=>uploadFiles(Array.from(e.target.files)));
$('paste-files').addEventListener('paste',e=> {
 e.preventDefault(); $('paste-files').value='';
 if(uploading) {feedback('upload-result','当前文件仍在发送，请等本次确认后再粘贴。','warning');return;}
 const data=e.clipboardData;
 let files=data && data.files?Array.from(data.files):[];
 if(!files.length && data && data.items) files=Array.from(data.items).filter(item=>item.kind==='file').map(item=>item.getAsFile()).filter(Boolean);
 if(!files.length) {feedback('upload-result','本次粘贴没有提供图片或文件。可用“选择文件或照片”；文字请到“文字”页发送。','warning');return;}
 return uploadFiles(files);
});
$('cancel-upload').addEventListener('click',()=> { stopUploads=true; if(activeUpload) activeUpload.abort(); });
const drop=$('drop-zone'); ['dragenter','dragover'].forEach(event=>drop.addEventListener(event,e=> { e.preventDefault(); drop.classList.add('dragging'); }));
['dragleave','drop'].forEach(event=>drop.addEventListener(event,e=> { e.preventDefault(); drop.classList.remove('dragging'); }));
drop.addEventListener('drop',e=>uploadFiles(Array.from(e.dataTransfer.files)));

function setupGuide() { $('receive-url').textContent=location.origin+'/v1/clipboard.txt'; $('send-url').textContent=location.origin+'/v1/clipboard'; $('sync-url').textContent=location.origin+'/v1/sync'; $('ack-url').textContent=location.origin+'/v1/ack'; $('auth-value').textContent=`Bearer ${token}`; }
async function copyGuide(text) { const ok=await copyText(text); feedback('guide-result',ok?'已复制。请在系统“快捷指令”中按说明手动配置。':'浏览器未允许复制，请长按选中文字复制。',ok?'success':'warning'); }
$('copy-receive-url').addEventListener('click',()=>copyGuide(location.origin+'/v1/clipboard.txt'));
$('copy-send-url').addEventListener('click',()=>copyGuide(location.origin+'/v1/clipboard'));
$('copy-auth').addEventListener('click',()=>copyGuide(`Bearer ${token}`));
$('copy-sync-url').addEventListener('click',()=>copyGuide(location.origin+'/v1/sync'));
$('copy-ack-url').addEventListener('click',()=>copyGuide(location.origin+'/v1/ack'));
$('copy-setup').addEventListener('click',()=>copyGuide(`轻桥 1.4 · 此配置仅放入自己的快捷指令，不要分享\n\n指令名称：轻桥同步\n同步地址：${location.origin}/v1/sync\n确认地址：${location.origin}/v1/ack\n两次请求方法：POST\n两次请求头名称：Authorization\n两次请求头值：Bearer ${token}\n同步请求体：JSON，mode 文本 smart，text 选择“获取剪贴板”的输出变量\n仅当返回 copy 为 true：复制返回 text，然后 POST 确认地址，JSON receipt 选择同步结果的 receipt。不要把固定文字“剪贴板”填入 text，也不要直接复制整个 JSON。\n首次运行只初始化；此后两端任意一端复制新文字，再运行同一指令。手机文字变化就发送，否则接收两分钟内的新内容；超时后请在电脑重新复制。两边都变化时本次手机文字优先，无需选择。系统不提供 iPhone 的真实复制时间，因此这按观察到的变化排序。\n最后显示同步结果的 message 通知，并绑定背面轻点两下。`));
$('unpair').addEventListener('click',async()=> { if(!confirm('断开后，网页和已配置的快捷指令均需重新配对。继续？'))return; try { await api('/v1/unpair',{method:'POST'}); stopUploads=true;if(activeUpload)activeUpload.abort();storeToken('');$('auth-value').textContent='';showPair();resetPair();feedback('pair-error','此设备权限已撤销。','warning'); } catch(error) {if(!error.silent)toast(errorText(error));} });
async function initialize() {
 await configureConnection();
 if(pairing && token) {
  try { const data=await api('/v1/state'); pairing='';$('qr-hint').hidden=true;showPaired();online(true);render(data);setupGuide();poll();return; }
  catch(error) { if(error.status===401)storeToken('');else {showPaired();online(false,error);setupGuide();poll();return;} }
 }
 if(pairing) {showPair();$('pair-form').requestSubmit();}else if(token) {showPaired();online(false);setupGuide();poll();}else showPair();
}
initialize();
