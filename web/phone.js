// 轻桥 2.0 · 手机网页（iPhone / 安卓，不用装 App）
const $ = id => document.getElementById(id);
// 同一个网络里是 http://电脑地址/，经中转是 https://中转/m/电脑ID/：接口都相对这个路径。
const BASE = location.pathname.replace(/[^/]*$/, '') || '/';
const U = p => BASE + p.replace(/^\//, '');
const viaRelay = BASE !== '/';
const K = 'lb2:' + (viaRelay ? BASE : '');
let token = localStorage.getItem(K + 't') || '';
let S = null, rev = 0, view = 'main', uploads = [], sendToClip = localStorage.getItem(K + 'clip') !== '0', alive = true;

function guessName() {
  const ua = navigator.userAgent;
  if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'iPad';
  if (/iPhone/.test(ua)) return 'iPhone';
  if (/Android/.test(ua)) { const m = ua.match(/Android [^;]+; ([^;)]+?)( Build|\))/); return m ? m[1].trim().slice(0, 24) : '安卓手机'; }
  return '手机';
}

async function call(path, body, extra) {
  const r = await fetch(U(path), {
    method: body === undefined ? 'GET' : 'POST',
    headers: Object.assign({ 'Content-Type': 'application/json', 'X-LB-Token': token }, extra || {}),
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) { const e = new Error(j.error || ('出错了（' + r.status + '）')); e.status = r.status; throw e; }
  return j;
}

// ───────────── 连接 ─────────────

async function boot() {
  // 配对码在 ?p=（二维码）或 #p=（旧二维码）里
  const h = new URLSearchParams(location.hash.slice(1)), qs = new URLSearchParams(location.search), secret = h.get('p') || qs.get('p');
  if (h.get('t')) saveToken(h.get('t')); // 从局域网网页换到中转地址时带过来的授权
  if (h.get('moved')) localStorage.setItem(K + 'moved', '1');
  if (secret || h.get('t')) history.replaceState(null, '', BASE);
  if (h.has('guide')) view = 'guide';
  if (secret && token) { try { await call('/api/phone/state'); } catch (e) { if (e.status === 401) token = ''; } }
  if (secret && !token) {
    $('app').innerHTML = `<div class="card pair"><span class="spin"></span><p style="margin-top:14px">正在连接电脑…</p></div>`;
    try {
      const r = await call('/api/phone/pair', { secret, name: guessName() });
      saveToken(r.token); toast('已连接 ' + r.pc);
    } catch (e) { showJoin(e.message); return; }
  }
  if (!token) { showJoin(); return; }
  loop();
}
function saveToken(t) { token = t; localStorage.setItem(K + 't', t); }

function showJoin(err) {
  setConn('', false);
  $('app').innerHTML = `<div class="card pair">
    <img class="logo" src="assets/icon.svg" alt="">
    <h1>连接这台电脑</h1>
    <p>连一次就好，以后打开这个网页就能互传。</p>
    ${err ? `<div class="banner" style="margin-bottom:14px">${esc(err)}</div>` : ''}
    <input class="input" id="name" value="${esc(guessName())}" maxlength="30" style="height:46px;font-size:16px;text-align:center">
    <button class="btn primary big" id="join" style="width:100%;margin-top:12px">连接</button>
    <p class="hint" style="margin-top:14px">更快的办法：用相机扫电脑轻桥上的二维码，扫完自动连上。</p>
  </div>`;
  $('join').onclick = async () => {
    try {
      const r = await call('/api/phone/join', { name: $('name').value.trim() || guessName() });
      $('app').innerHTML = `<div class="card pair">
        <img class="logo" src="assets/icon.svg" alt="">
        <h1>到电脑上点“允许”</h1>
        <p>电脑上会弹出请求，核对码：</p>
        <div class="code">${esc(r.code)}</div>
        <p style="margin-top:14px;display:flex;gap:8px;justify-content:center;align-items:center"><span class="spin"></span><span class="muted">等待中…</span></p>
      </div>`;
      for (let i = 0; i < 130; i++) {
        await sleep(1000);
        const p = await call('/api/phone/join/poll', { req: r.req });
        if (p.status === 'ok') { saveToken(p.token); toast('已连接 ' + p.pc); loop(); return; }
        if (p.status === 'rejected') { showJoin('电脑上点了拒绝'); return; }
        if (p.status === 'expired') { showJoin('等太久了，请再试一次'); return; }
      }
      showJoin('等太久了，请再试一次');
    } catch (e) { showJoin(e.message); }
  };
}

async function loop() {
  let first = true;
  while (alive) {
    try {
      const r = await fetch(U('/api/phone/state?rev=' + (first ? 0 : rev)), { headers: { 'X-LB-Token': token } });
      if (r.status === 401) { localStorage.removeItem(K + 't'); token = ''; const j = await r.json().catch(() => ({})); showJoin(j.error); return; }
      S = await r.json(); rev = S.rev; first = false;
      if (!viaRelay && S.relay) { alive = false; location.replace(S.relay + '#t=' + encodeURIComponent(token) + '&moved=1'); return; }
      render();
      refreshCloud();
    } catch { setConn('连不上电脑', false); await sleep(2000); }
  }
}
document.addEventListener('visibilitychange', () => { if (!document.hidden && S) { rev = 0; } });

function setConn(text, ok) { $('conn').innerHTML = text ? `<i class="dot ${ok ? 'on' : ''}"></i>${esc(text)}` : ''; }

// ───────────── 主页面 ─────────────

function fileUrl(q) { return U(`/api/phone/file?${q}&t=${encodeURIComponent(token)}`); }

function clipCard() {
  const c = S.clip;
  let body;
  if (!c) body = `<div class="empty">在电脑上复制点什么，这里马上就会出现。</div>`;
  else if (c.kind === 'text') body = `<div class="clip-text" id="clipText">${esc(c.text)}</div>
     <div class="actions"><button class="btn primary big" id="copy">${icon('copy')}复制到手机</button></div>`;
  else if (c.kind === 'image' && c.items[0] && c.items[0].ok) {
    const u = fileUrl(`ev=${c.id}&i=0`);
    body = `<img class="clip-img" src="${u}&view=1" alt="">
      <div class="actions"><a class="btn primary big" href="${u}" download="${esc(c.items[0].name)}">${icon('download')}保存图片</a></div>
      <div class="hint">也可以长按图片 → 存储到“照片”或拷贝。</div>`;
  } else body = (c.items || []).map((x, i) => `<div class="file"><div class="fic">${icon('file')}</div><div class="fb"><div class="fn">${esc(x.name)}</div><div class="fs">${size(x.size)}</div></div>${x.ok ? `<a class="btn soft small" href="${fileUrl(`ev=${c.id}&i=${i}`)}" download="${esc(x.name)}">${icon('download')}下载</a>` : '<span class="muted">已不在</span>'}</div>`).join('');
  const from = c ? `<span class="muted">${c.fromMe ? '这部手机发的' : '来自 ' + esc(c.from)} · ${when(c.time)}</span>` : '';
  return `<div class="card"><div class="ct">${icon('text')}电脑剪贴板${from}</div>${body}</div>`;
}

// 能直接读剪贴板时（HTTPS，经中转打开）一个按钮就发；局域网 http 下浏览器不让读，只能粘贴。
const canRead = () => !!(window.isSecureContext && navigator.clipboard && navigator.clipboard.readText);

function sendCard() {
  return `<div class="card"><div class="ct">${icon('send')}发给电脑</div>
    <div class="how">手机上复制的东西不会自己过去（iPhone 不允许网页偷看剪贴板）。<b>${canRead() ? '点下面的按钮，或长按框里 → 粘贴' : '长按下面的框 → 粘贴'}</b>，就直接进电脑剪贴板。</div>
    ${canRead() ? `<button class="btn primary big" id="readClip" style="width:100%;margin-bottom:10px">${icon('copy')}发送手机剪贴板</button>` : ''}
    <textarea class="input" id="text" placeholder="长按这里 → 粘贴，自动发到电脑"></textarea>
    <div class="actions">
      <button class="btn line big" id="pick">${icon('image')}照片 / 文件</button>
      <button class="btn primary big" id="sendText">${icon('send')}发送文字</button>
    </div>
    <label class="check"><span class="switch"><input type="checkbox" id="toClip" ${sendToClip ? 'checked' : ''}><span></span></span>照片、文件也放进电脑剪贴板（电脑上直接 Ctrl+V）</label>
    <div id="ups">${upsHtml()}</div>
    <input type="file" id="files" multiple hidden>
  </div>`;
}

function upsHtml(cloud) {
  return uploads.filter(u => !!u.cloud === !!cloud).map(u => `<div class="up"><div class="row"><span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:70%">${esc(u.name)}</span><span>${u.err ? '<span style="color:var(--bad)">' + esc(u.err) + '</span>' : u.done ? (u.cloud ? '✓ 电脑正在放到小云' : '✓ 已发到电脑') : Math.round(u.p * 100) + '%'}</span></div>${u.done || u.err ? '' : `<div class="bar"><i style="width:${(u.p * 100).toFixed(1)}%"></i></div>`}</div>`).join('');
}

// 小云：电脑那边的文件池。手机点“下载”打开链接，下载页在手机上解密（电脑关着也能下）。
let cloudList = null, cloudAt = 0, cloudKey = null;
async function refreshCloud() {
  const k = S && S.cloud; if (k === undefined || k < 0) { cloudList = null; return; }
  if (k === cloudKey && Date.now() - cloudAt < 60000) return;
  cloudKey = k; cloudAt = Date.now();
  try { const r = await call('/api/phone/cloud'); cloudList = r.ready ? r : null; render(); } catch { }
}
function cloudCard() {
  if (!cloudList) return '';
  return `<div class="card"><div class="ct">${icon('cloud')}小云<span class="muted" style="margin-left:auto;font-weight:400;font-size:12px">${cloudList.usedMB} / ${cloudList.capMB} MB</span></div>
    ${cloudList.files.length ? '' : '<div class="empty">放进来的照片、文件，组里的电脑和这部手机在哪都能下载，也可以把链接发给别人。放满 1 GB 会先删最早的。</div>'}
    ${cloudList.files.slice(0, 20).map(x => `<div class="file"><div class="fic">${icon('file')}</div><div class="fb"><div class="fn">${esc(x.name)}</div><div class="fs">${size(x.size)} · ${when(x.at)}${x.from ? ' · ' + esc(x.from) : ''}</div></div><a class="btn soft small" href="${esc(x.link)}" target="_blank" rel="noopener">${icon('download')}下载</a></div>`).join('')}
    <div class="actions"><button class="btn soft big" id="cloudPick">${icon('upload')}放照片 / 文件到小云</button></div>
    <div id="cups">${upsHtml(true)}</div>
    <input type="file" id="cloudFiles" multiple hidden>
  </div>`;
}

function inboxCard() {
  if (!S.inbox || !S.inbox.length) return '';
  return `<div class="card"><div class="ct">${icon('download')}电脑发来的文件</div>${S.inbox.map(x => `<div class="file"><div class="fic">${icon('file')}</div><div class="fb"><div class="fn">${esc(x.name)}</div><div class="fs">${size(x.size)} · ${when(x.time)}${x.got ? ' · 已下载' : ''}</div></div><a class="btn ${x.got ? 'line' : 'soft'} small" href="${fileUrl('inbox=' + x.id)}" download="${esc(x.name)}">${icon('download')}下载</a></div>`).join('')}</div>`;
}

const standalone = () => window.navigator.standalone || matchMedia('(display-mode: standalone)').matches;
const isIOS = () => /iPhone|iPad|iPod/.test(navigator.userAgent) || (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1);

function render() {
  setConn(S.pc, true);
  if (view === 'guide') { renderGuide(); return; }
  const keepText = $('text') ? $('text').value : '';
  const focused = document.activeElement && document.activeElement.id === 'text';
  if (focused) { // 正在输入时只更新剪贴板卡片，不打断输入
    const box = document.querySelector('#clipBox'); if (box) box.innerHTML = clipCard(); bindClip(); return;
  }
  const tip = isIOS() && !standalone() && localStorage.getItem(K + 'tip') !== '1';
  $('app').innerHTML = `
    ${S.paused ? '<div class="banner">电脑上的轻桥暂停了同步，恢复后才能互传。</div>' : ''}
    ${viaRelay && localStorage.getItem(K + 'moved') === '1' ? `<div class="card tipcard"><div class="ct">${icon('plus')}换成了在哪都能用的地址<button class="btn ghost small" id="movedX" style="margin-left:auto">知道了</button></div>以后手机用流量、在别的 Wi-Fi 也能打开。<b>请重新放一次到主屏幕：</b>点 Safari 底部的“分享” → “添加到主屏幕”，再把主屏幕上旧的“轻桥”图标删掉（旧图标只在家里的 Wi-Fi 能用）。${isIOS() ? '设置过“轻点背面”的，点下面“设置轻点背面”，按第 1 步复制新地址替换快捷指令里的旧地址。' : ''}</div>` : ''}
    <div id="clipBox">${clipCard()}</div>
    ${sendCard()}
    ${inboxCard()}
    ${cloudCard()}
    ${isIOS() && localStorage.getItem(K + 'tapTip') !== '1' ? `<div class="card tipcard"><div class="ct">${icon('hand')}更省事：轻点手机背面<button class="btn ghost small" id="tapX" style="margin-left:auto">不用了</button></div>设置一次（约 1 分钟），以后手机上复制完<b>轻点背面两下</b>就发到电脑，不用打开这个网页；电脑上复制的也能这样取回手机。还可以设成<b>离开微信等 App 时自动发送</b>，连背面都不用点。<div class="actions" style="margin-top:10px"><button class="btn soft" id="guide2">${icon('hand')}看怎么设置</button></div></div>` : ''}
    ${tip ? `<div class="card" style="font-size:14px"><div class="ct">${icon('plus')}放到主屏幕<button class="btn ghost small" id="tipX" style="margin-left:auto">知道了</button></div>点 Safari 底部的“分享”按钮 → “添加到主屏幕”。以后像 App 一样一点就开，不用再扫码。</div>` : ''}
    <div class="links">
      ${!viaRelay && S.relay ? '<button id="roam">离开这个 Wi-Fi 也能用</button>' : ''}
      ${isIOS() ? '<button id="guide">设置“轻点背面”同步文字</button>' : ''}
      <button id="forget">断开这台电脑</button>
    </div>`;
  if ($('text')) $('text').value = keepText;
  bind();
}

function bindClip() {
  const c = $('copy');
  if (c) c.onclick = () => { const ok = copyText(S.clip.text); toast(ok ? '已复制，可以去粘贴了' : '复制没成功，长按上面的文字手动复制', !ok); };
}

function bind() {
  bindClip();
  $('sendText').onclick = () => sendText($('text').value);
  $('text').addEventListener('paste', e => {
    const files = e.clipboardData && [...e.clipboardData.files];
    if (files && files.length) { e.preventDefault(); upload(files); return; }
    setTimeout(() => { const v = $('text').value; if (v.trim()) sendText(v); }, 30);
  });
  $('pick').onclick = () => $('files').click();
  $('files').onchange = () => { upload([...$('files').files]); $('files').value = ''; };
  $('toClip').onchange = e => { sendToClip = e.target.checked; localStorage.setItem(K + 'clip', sendToClip ? '1' : '0'); };
  if ($('tipX')) $('tipX').onclick = () => { localStorage.setItem(K + 'tip', '1'); render(); };
  if ($('movedX')) $('movedX').onclick = () => { localStorage.removeItem(K + 'moved'); render(); };
  if ($('cloudPick')) $('cloudPick').onclick = () => $('cloudFiles').click();
  if ($('cloudFiles')) $('cloudFiles').onchange = () => { upload([...$('cloudFiles').files], true); $('cloudFiles').value = ''; };
  if ($('guide')) $('guide').onclick = () => { view = 'guide'; render(); scrollTo(0, 0); };
  if ($('guide2')) $('guide2').onclick = () => { view = 'guide'; render(); scrollTo(0, 0); };
  if ($('tapX')) $('tapX').onclick = () => { localStorage.setItem(K + 'tapTip', '1'); render(); };
  if ($('readClip')) $('readClip').onclick = async () => {
    try {
      const t = await navigator.clipboard.readText();
      if (t && t.trim()) { sendText(t); return; }
    } catch { }
    // 剪贴板里是图片，或者浏览器没让读：退回粘贴
    $('text').focus(); toast('长按框里 → 粘贴', true);
  };
  if ($('roam')) $('roam').onclick = () => {
    if (!confirm('换成经中转的地址：手机用流量或在别的 Wi-Fi 也能用。换过去以后，请在新网页里重新“添加到主屏幕”。')) return;
    location.href = S.relay + '#t=' + encodeURIComponent(token);
  };
  $('forget').onclick = async () => {
    if (!confirm('断开后需要重新扫码才能再连。确定？')) return;
    try { await call('/api/phone/forget', {}); } catch { }
    localStorage.removeItem(K + 't'); token = ''; alive = false; showJoin();
  };
}

async function sendText(v) {
  if (!v || !v.trim()) { toast('先粘贴或输入文字', true); return; }
  try { await call('/api/phone/text', { text: v }); $('text').value = ''; $('text').blur(); toast('已发到电脑，Ctrl+V 就能粘贴'); }
  catch (e) { toast(e.message, true); }
}

function copyText(text) {
  try {
    const ta = document.createElement('textarea');
    ta.value = text; ta.style.cssText = 'position:fixed;top:0;left:0;opacity:0;font-size:16px';
    document.body.appendChild(ta); ta.focus(); ta.select(); ta.setSelectionRange(0, text.length);
    const ok = document.execCommand('copy'); ta.remove(); window.getSelection().removeAllRanges();
    return ok;
  } catch { return false; }
}

async function upload(files, cloud) {
  if (!files.length) return;
  const group = Math.random().toString(36).slice(2, 10), count = files.length;
  const batch = files.map(f => ({ name: f.name || 'image.png', p: 0, file: f, cloud: !!cloud }));
  uploads = batch.concat(uploads).slice(0, 12);
  refreshUps();
  for (const u of batch) {
    try {
      await new Promise((ok, bad) => {
        const x = new XMLHttpRequest();
        x.open('POST', U(`/api/phone/upload?name=${encodeURIComponent(u.name)}&group=${group}&count=${count}&clip=${sendToClip && !cloud ? 1 : 0}${cloud ? '&to=cloud' : ''}`));
        x.setRequestHeader('X-LB-Token', token);
        x.upload.onprogress = e => { if (e.lengthComputable) { u.p = e.loaded / e.total; refreshUps(); } };
        x.onload = () => { if (x.status === 200) ok(); else { let m = '失败'; try { m = JSON.parse(x.responseText).error || m; } catch { } bad(new Error(m)); } };
        x.onerror = () => bad(new Error('网络断了'));
        x.send(u.file);
      });
      u.done = true;
    } catch (e) { u.err = e.message; }
    refreshUps();
  }
  if (batch.every(u => u.done)) toast(cloud ? '电脑收到了，正在放到小云，好了会出现在上面' : sendToClip ? '已发到电脑，Ctrl+V 就能粘贴' : '已发到电脑');
}
function refreshUps() { const el = $('ups'); if (el) el.innerHTML = upsHtml(); const c = $('cups'); if (c) c.innerHTML = upsHtml(true); }

// ───────────── 轻点背面（快捷指令） ─────────────

// 快捷指令用哪个地址：有中转用中转（在哪都能用）；局域网里先试电脑名字（换 IP 也不用改），手机解析不了再用 IP。
let guideHost = null;
async function pickHost() {
  if (viaRelay) return { url: location.origin + BASE, how: '经中转，手机在哪个网络都能用' };
  if (S.relay) return { url: S.relay, how: '经中转，手机在哪个网络都能用' };
  if (S.mdns) {
    const u = `http://${S.mdns}:${S.port}/`;
    const ok = await Promise.race([fetch(u + 'favicon.ico?probe=' + Date.now(), { mode: 'no-cors', cache: 'no-store' }).then(() => true, () => false), sleep(2500).then(() => false)]);
    if (ok) return { url: u, how: '用的是电脑的名字，电脑换了 IP 也不用改' };
  }
  return { url: location.origin + '/', how: '用的是电脑现在的 IP。电脑换了网络后，回这里复制新地址替换' };
}

function renderGuide() {
  const host = guideHost || { url: viaRelay ? location.origin + BASE : location.origin + '/', how: '正在检查哪个地址能用…' };
  // 授权放在地址里，快捷指令就不用另外加“头部”。
  const url = host.url + 'v1/swap?t=' + encodeURIComponent(token);
  $('app').innerHTML = `<button class="back" id="back">${icon('back')}返回</button>
    <div class="card guide">
      <div class="ct">${icon('hand')}轻点手机背面两下，同步文字</div>
      <p class="lead">设置一次（约 2 分钟）。以后手机上复制完<b>轻点背面两下</b>，电脑上就能 Ctrl+V；电脑上刚复制的文字，也是轻点两下就到手机。</p>
      <div class="step0">
        <div><b>先复制这个地址</b>（里面带着这部手机的授权，别发给别人）</div>
        <div class="kv"><code>${esc(url)}</code><button class="btn primary small" data-copy="${esc(url)}">${icon('copy')}复制</button></div>
        <div class="hint" style="margin-top:4px">${esc(host.how)}</div>
      </div>
      <div class="sub">在“快捷指令”App 里新建</div>
      <ol>
        <li>打开<b>快捷指令</b> App，点右上角 <b>+</b>。<div class="warn">下面会列出“下一个操作建议”（比如微信的“发送信息”），<b>不要点</b>，只用底部的搜索框。</div></li>
        <li>点底部<b>“搜索操作”</b>，输入<b>剪贴板</b>，点<b>“获取剪贴板”</b>。</li>
        <li>再点“搜索操作”，输入 <b>URL</b>，点<b>“获取 URL 内容”</b>。</li>
        <li>点这一步里蓝色的 <b>URL</b>，<b>粘贴</b>刚才复制的地址。</li>
        <li>点这一步右边的 <b>⌄</b>（或“显示更多”）展开：<b>方法</b>改成 <b>POST</b>；<b>请求体</b>选<b>文件</b>；点下面的“文件”，选<b>“剪贴板”</b>（第 2 步的结果）。</li>
        <li>再搜索<b>拷贝</b>，点<b>“拷贝至剪贴板”</b>。它会自动拷贝“URL 的内容”，不用改。</li>
        <li>点最上面的名字（例如“获取剪贴板 ⌄”）→ <b>重新命名</b>，改成<b>轻桥</b>，点<b>完成</b>。</li>
      </ol>
      <div class="sub">让背面轻点两下运行它</div>
      <ol start="8">
        <li>打开 iPhone <b>设置 → 辅助功能 → 触控 → 轻点背面 → 轻点两下</b>，往下翻到“快捷指令”，选<b>轻桥</b>。</li>
      </ol>
      <div class="hint">试一下：在别的 App 里复制一段字，轻点背面两下，电脑上按 Ctrl+V。第一次可能弹“允许粘贴”，点允许。<br>
        ${S.relay || viaRelay ? '' : '手机和电脑要在同一个 Wi-Fi。'}弄错了就在快捷指令里删掉“轻桥”，从第 1 步重来。</div>
    </div>
    <div class="card guide">
      <div class="ct">${icon('refresh')}连背面都不想点？（可选，iOS 17 及以上）</div>
      <p class="lead">在微信等 App 里复制完，<b>切到别的 App 时</b>自动运行“轻桥”。</p>
      <ol>
        <li>快捷指令 App 底部点<b>“自动化”</b>，再点右上角 <b>+</b>（第一次是中间的“新建自动化”）。</li>
        <li>往下找到<b>“App”</b>，点进去。</li>
        <li>“App”那一行点<b>“选取”</b>，勾上常复制东西的 App（微信、Safari、备忘录……），点<b>完成</b>。</li>
        <li>勾<b>“已关闭”</b>、取消“已打开”；下面选<b>“立即运行”</b>（“运行时通知”可以关掉），点右上角<b>下一步</b>。</li>
        <li>在“我的快捷指令”里点<b>轻桥</b>（不要点“新建空白自动化”）。</li>
      </ol>
      <div class="hint">之后每次离开这些 App 都会运行一次：手机刚复制的就发到电脑（不会被电脑上的覆盖）；手机没复制新东西、电脑 5 分钟内复制过文字的，放进手机剪贴板；都没有就什么也不变。如果每次都问“允许粘贴”：打开 iPhone 设置 → 快捷指令 → 从其他 App 粘贴 → 允许。</div>
    </div>`;
  $('back').onclick = () => { view = 'main'; render(); };
  document.querySelectorAll('[data-copy]').forEach(b => b.onclick = () => toast(copyText(b.dataset.copy) ? '已复制' : '复制没成功，长按手动复制', false));
  if (!guideHost && !renderGuide.busy) { renderGuide.busy = true; pickHost().then(h => { guideHost = h; renderGuide.busy = false; if (view === 'guide') renderGuide(); }); }
}

boot();
