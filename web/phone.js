// 轻桥 2.0 · 手机网页（iPhone / 安卓，不用装 App）
const $ = id => document.getElementById(id);
const K = 'lb2:';
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
  const r = await fetch(path, {
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
  const h = new URLSearchParams(location.hash.slice(1)), secret = h.get('p');
  if (secret) history.replaceState(null, '', '/');
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
    <img class="logo" src="/assets/icon.svg" alt="">
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
        <img class="logo" src="/assets/icon.svg" alt="">
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
      const r = await fetch('/api/phone/state?rev=' + (first ? 0 : rev), { headers: { 'X-LB-Token': token } });
      if (r.status === 401) { localStorage.removeItem(K + 't'); token = ''; const j = await r.json().catch(() => ({})); showJoin(j.error); return; }
      S = await r.json(); rev = S.rev; first = false;
      render();
    } catch { setConn('连不上电脑', false); await sleep(2000); }
  }
}
document.addEventListener('visibilitychange', () => { if (!document.hidden && S) { rev = 0; } });

function setConn(text, ok) { $('conn').innerHTML = text ? `<i class="dot ${ok ? 'on' : ''}"></i>${esc(text)}` : ''; }

// ───────────── 主页面 ─────────────

function fileUrl(q) { return `/api/phone/file?${q}&t=${encodeURIComponent(token)}`; }

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

function sendCard() {
  return `<div class="card"><div class="ct">${icon('send')}发给电脑</div>
    <textarea class="input" id="text" placeholder="在这里粘贴，就会直接发到电脑剪贴板"></textarea>
    <div class="actions">
      <button class="btn line big" id="pick">${icon('image')}照片 / 文件</button>
      <button class="btn primary big" id="sendText">${icon('send')}发送文字</button>
    </div>
    <label class="check"><span class="switch"><input type="checkbox" id="toClip" ${sendToClip ? 'checked' : ''}><span></span></span>照片、文件也放进电脑剪贴板（电脑上直接 Ctrl+V）</label>
    <div id="ups">${upsHtml()}</div>
    <input type="file" id="files" multiple hidden>
  </div>`;
}

function upsHtml() {
  return uploads.map(u => `<div class="up"><div class="row"><span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:70%">${esc(u.name)}</span><span>${u.err ? '<span style="color:var(--bad)">' + esc(u.err) + '</span>' : u.done ? '✓ 已发到电脑' : Math.round(u.p * 100) + '%'}</span></div>${u.done || u.err ? '' : `<div class="bar"><i style="width:${(u.p * 100).toFixed(1)}%"></i></div>`}</div>`).join('');
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
    <div id="clipBox">${clipCard()}</div>
    ${sendCard()}
    ${inboxCard()}
    ${tip ? `<div class="card" style="font-size:14px"><div class="ct">${icon('plus')}放到主屏幕<button class="btn ghost small" id="tipX" style="margin-left:auto">知道了</button></div>点 Safari 底部的“分享”按钮 → “添加到主屏幕”。以后像 App 一样一点就开，不用再扫码。</div>` : ''}
    <div class="links">
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
  if ($('guide')) $('guide').onclick = () => { view = 'guide'; render(); scrollTo(0, 0); };
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

async function upload(files) {
  if (!files.length) return;
  const group = Math.random().toString(36).slice(2, 10), count = files.length;
  const batch = files.map(f => ({ name: f.name || 'image.png', p: 0, file: f }));
  uploads = batch.concat(uploads).slice(0, 12);
  refreshUps();
  for (const u of batch) {
    try {
      await new Promise((ok, bad) => {
        const x = new XMLHttpRequest();
        x.open('POST', `/api/phone/upload?name=${encodeURIComponent(u.name)}&group=${group}&count=${count}&clip=${sendToClip ? 1 : 0}`);
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
  if (batch.every(u => u.done)) toast(sendToClip ? '已发到电脑，Ctrl+V 就能粘贴' : '已发到电脑');
}
function refreshUps() { const el = $('ups'); if (el) el.innerHTML = upsHtml(); }

// ───────────── 轻点背面（快捷指令） ─────────────

function renderGuide() {
  const host = S.mdns ? `http://${S.mdns}:${S.port}` : location.origin;
  const url = host + '/v1/swap', ipUrl = location.origin + '/v1/swap', auth = 'Bearer ' + token;
  $('app').innerHTML = `<button class="back" id="back">${icon('back')}返回</button>
    <div class="card guide">
      <div class="ct">${icon('hand')}轻点手机背面，同步文字</div>
      <p style="margin:0 0 6px;color:var(--ink2);font-size:14px">设置一次，以后：<br>电脑上复制 → 手机背面<b>轻点两下</b> → 手机里粘贴；<br>手机上复制 → 轻点两下 → 电脑上 Ctrl+V。</p>
      <ol>
        <li>打开“<b>快捷指令</b>”App，点右上角 <b>+</b> 新建一个，名字叫“轻桥”。</li>
        <li>添加操作“<b>获取剪贴板</b>”。</li>
        <li>添加操作“<b>获取 URL 内容</b>”，填：
          <div class="kv"><code>${esc(url)}</code><button class="btn soft small" data-copy="${esc(url)}">复制</button></div>
          <div style="font-size:13px;color:var(--muted);margin-top:4px">展开后：方法选 <b>POST</b>；头部加一项，键 <b>Authorization</b>，值：</div>
          <div class="kv"><code>${esc(auth)}</code><button class="btn soft small" data-copy="${esc(auth)}">复制</button></div>
          <div style="font-size:13px;color:var(--muted);margin-top:4px">请求体选 <b>文件</b>，文件选“剪贴板”。</div>
        </li>
        <li>添加操作“<b>拷贝至剪贴板</b>”（拷贝的就是“URL 的内容”）。</li>
        <li>打开 iPhone <b>设置 → 辅助功能 → 触控 → 轻点背面 → 轻点两下</b>，选“轻桥”。</li>
      </ol>
      <div class="hint">两边都没有新内容时什么也不变。地址打不开时，把第 3 步的地址换成 <code class="mono">${esc(ipUrl)}</code>。这个授权码只给这部手机用，别发给别人。</div>
    </div>`;
  $('back').onclick = () => { view = 'main'; render(); };
  document.querySelectorAll('[data-copy]').forEach(b => b.onclick = () => toast(copyText(b.dataset.copy) ? '已复制' : '复制没成功，长按手动复制', false));
}

boot();
