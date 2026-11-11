// 轻桥 2.0 · 手机网页（iPhone / 安卓，不用装 App）
const $ = id => document.getElementById(id);
// 同一个网络里是 http://电脑地址/，经中转是 https://中转/m/电脑ID/：接口都相对这个路径。
const BASE = location.pathname.replace(/[^/]*$/, '') || '/';
const U = p => BASE + p.replace(/^\//, '');
const viaRelay = BASE !== '/';
const K = 'lb2:' + (viaRelay ? BASE : '');
let token = localStorage.getItem(K + 't') || '';
let S = null, rev = 0, offline = false, view = 'main', uploads = [], sendToClip = localStorage.getItem(K + 'clip') !== '0', alive = true;

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
  if (h.has('guide') || localStorage.getItem(K + 'wz')) view = 'wizard';
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
      if (r.status === 401) { localStorage.removeItem(K + 't'); token = ''; const j = await r.json().catch(() => ({})); $('tabbar').hidden = true; document.body.classList.remove('tabbed'); showJoin(j.error); return; }
      if (!r.ok) { // 中转在，电脑不在（关机、睡眠、轻桥没运行）：留在原页面等它回来
        offline = true; if (S) render(); else { setConn('电脑不在线', false); $('app').innerHTML = offlineCard(); }
        await sleep(4000); continue;
      }
      offline = false;
      S = await r.json(); rev = S.rev; first = false;
      if (!viaRelay && S.relay) { alive = false; location.replace(S.relay + '#t=' + encodeURIComponent(token) + '&moved=1'); return; }
      render();
      refreshCloud(); refreshHist();
    } catch { setConn(navigator.onLine === false ? '手机没有网络' : '连不上电脑', false); await sleep(2000); }
  }
}
document.addEventListener('visibilitychange', () => { if (!document.hidden && S) { rev = 0; } });

function offlineCard() {
  return `<div class="card pair"><img class="logo" src="assets/icon.svg" alt=""><h1>电脑现在不在线</h1><p>电脑关机、睡眠了，或者上面的轻桥没在运行。</p><p style="margin-top:14px;display:flex;gap:8px;justify-content:center;align-items:center"><span class="spin"></span><span class="muted">电脑一回来这里会自己连上，不用重新扫码</span></p></div>`;
}
function setConn(text, ok) { $('conn').innerHTML = text ? `<i class="dot ${ok ? 'on' : ''}"></i>${esc(text)}` : ''; }

// ───────────── 主页面 ─────────────

function fileUrl(q) { return U(`/api/phone/file?${q}&t=${encodeURIComponent(token)}`); }

function clipCard() {
  const c = S.clip;
  let body;
  if (!c) body = `<div class="empty">在电脑上复制点什么，这里马上就会出现。</div>`;
  else if (c.kind === 'text') body = `<div class="clip-text" id="clipText">${esc(c.text)}</div>
     <div class="actions"><button class="btn primary big" id="copy">${icon('copy')}${S.clip.id === copiedId ? '已复制 · 再复制一次' : '复制到手机'}</button></div>
     ${quickReady() ? '<div class="quickhint">电脑刚复制的 · 点屏幕任意空白处也能复制</div>' : ''}`;
  else if (c.kind === 'image' && c.items[0] && c.items[0].ok) {
    const u = fileUrl(`ev=${c.id}&i=0`);
    body = `<img class="clip-img" data-view="${u}" src="${u}&view=1&thumb=1200" alt="">
      <div class="actions"><button class="btn primary big" data-view="${u}">${icon('image')}看大图 / 保存</button></div>`;
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

// 之前的：电脑剪贴板的历史（文字点一下复制，图片点开保存）。可以清空，也可以设多少天自动清理。
let hist = null, histKey = null, histTab = 'all', histAll = false;
async function refreshHist(force) {
  const k = S && S.hist; if (k === undefined) return;
  if (!force && k === histKey) return;
  histKey = k;
  try { hist = await call('/api/phone/history'); render(); } catch { }
}
function histCard(full) {
  if (!hist) return '';
  const list = hist.items;
  const isImg = x => x.kind === 'image' && x.files[0];
  const texts = list.filter(x => x.kind === 'text'), imgs = list.filter(isImg), files = list.filter(x => x.kind !== 'text' && !isImg(x));
  const tabs = [['all', '全部'], ['text', '文字'], ['image', '图片'], ['file', '文件']].filter(([k]) => k === 'all' || (k === 'text' ? texts : k === 'image' ? imgs : files).length);
  if (!tabs.some(([k]) => k === histTab)) histTab = 'all';
  let shown = histTab === 'all' ? list : histTab === 'text' ? texts : histTab === 'image' ? imgs : files;
  const more = !histAll && shown.length > 30; if (more) shown = shown.slice(0, 30);
  const out = []; let grid = null;
  const flush = () => { if (grid) { out.push(`<div class="hgrid">${grid.join('')}</div>`); grid = null; } };
  for (const x of shown) {
    if (isImg(x)) {
      const u = fileUrl(`log=${x.id}&i=0`);
      (grid = grid || []).push(x.files[0].ok ? `<button class="himg" data-view="${u}"><img loading="lazy" src="${u}&view=1&thumb=360" alt=""><em>${when(x.time)}</em></button>` : `<span class="himg gone">已不在<em>${when(x.time)}</em></span>`);
      continue;
    }
    flush();
    if (x.kind === 'text') out.push(`<button class="hrow" data-hcopy="${x.id}"><span class="ht">${esc((x.text || '').slice(0, 300))}</span><span class="hm">${when(x.time)} · 点一下复制</span></button>`);
    else out.push(x.files.map((f, i) => `<div class="file"><div class="fic">${icon('file')}</div><div class="fb"><div class="fn">${esc(f.name)}</div><div class="fs">${size(f.size)} · ${when(x.time)}</div></div>${f.ok ? `<a class="btn soft small" href="${fileUrl(`log=${x.id}&i=${i}`)}" download="${esc(f.name)}">${icon('download')}下载</a>` : '<span class="muted">已不在</span>'}</div>`).join(''));
  }
  flush();
  return `<div class="card"><div class="ct">${icon('list')}电脑复制过的${tabs.length > 2 ? `<span class="htabs">${tabs.map(([k, l]) => `<button class="${histTab === k ? 'on' : ''}" data-htab="${k}">${l}</button>`).join('')}</span>` : ''}</div>
    ${list.length ? out.join('') : '<div class="empty">电脑上复制过的文字、图片会留在这里，方便回头找。</div>'}
    ${more ? '<button class="btn line" id="histMore" style="width:100%;margin-top:10px">看更多</button>' : ''}
    <div class="hfoot">只在这部手机上看的记录 · 自动清理 <select id="histDays">${[[1, '1 天'], [7, '7 天'], [30, '30 天'], [0, '不清理']].map(([v, l]) => `<option value="${v}" ${hist.days === v ? 'selected' : ''}>${l}</option>`).join('')}</select>${list.length ? '<button class="btn ghost small" id="histClear">清空</button>' : ''}</div>
  </div>`;
}
function bindHist() {
  document.querySelectorAll('[data-hcopy]').forEach(b => b.onclick = () => { const x = hist.items.find(i => i.id === b.dataset.hcopy); const ok = x && copyText(x.text); toast(ok ? '已复制，可以去粘贴了' : '复制没成功，长按文字手动复制', !ok); });
  document.querySelectorAll('[data-htab]').forEach(b => b.onclick = () => { histTab = b.dataset.htab; histAll = false; render(); });
  if ($('histMore')) $('histMore').onclick = () => { histAll = true; render(); };
  if ($('histDays')) $('histDays').onchange = async e => { try { await call('/api/phone/history-days', { days: +e.target.value }); toast('已改'); refreshHist(true); } catch (err) { toast(err.message, true); } };
  if ($('histClear')) $('histClear').onclick = async () => {
    if (!confirm('清空这部手机上看到的记录？\n\n电脑上的记录和文件不受影响。')) return;
    try { await call('/api/phone/history-clear', {}); toast('已清空'); refreshHist(true); } catch (err) { toast(err.message, true); }
  };
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

const inWeChat = () => /MicroMessenger/i.test(navigator.userAgent);
// iPhone 上“添加到主屏幕”藏在分享菜单里；新系统的 Safari 把分享按钮收进了地址栏旁的 ··· 里。
function homeHow() {
  if (inWeChat()) return '先点右上角 ··· → 在默认浏览器中打开，到 Safari 里再放。';
  if (isIOS()) return '在 Safari 里：点底部（或地址栏旁 <b>···</b> 里）的<b>分享</b>按钮（方框加向上箭头）→ 往下翻 → <b>添加到主屏幕</b>。列表里没有的话，翻到最下面点“编辑操作”把它加上。';
  return '点浏览器菜单 → “添加到主屏幕”。';
}
const standalone = () => window.navigator.standalone || matchMedia('(display-mode: standalone)').matches;
const isIOS = () => /iPhone|iPad|iPod/.test(navigator.userAgent) || (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1);

// ───────────── 三个页面：传送 / 历史 / 设置 ─────────────

const TABS = [['main', 'send', '传送'], ['hist', 'list', '历史'], ['set', 'gear', '设置']];
function go(v) { view = v; render(); scrollTo(0, 0); }
function drawTabs() {
  const bar = $('tabbar'); bar.hidden = false; document.body.classList.add('tabbed');
  const cur = view === 'wizard' ? 'set' : view;
  const dot = isIOS() && !S.auto && !inWeChat();
  bar.innerHTML = TABS.map(([k, ic, l]) => `<button class="${cur === k ? 'on' : ''}" data-tab="${k}">${icon(ic)}<span>${l}</span>${k === 'set' && dot ? '<i class="tdot"></i>' : ''}</button>`).join('');
  bar.querySelectorAll('[data-tab]').forEach(b => b.onclick = () => go(b.dataset.tab));
}

function banners() {
  return `${offline ? '<div class="banner bad"><b>电脑现在不在线</b>：电脑关机、睡眠了，或者上面的轻桥没在运行。电脑一回来这里会自己恢复，不用重新扫码。</div>' : ''}
    ${S.paused ? '<div class="banner">电脑上的轻桥暂停了同步，恢复后才能互传。</div>' : ''}
    ${inWeChat() ? '<div class="banner">现在是在微信里打开的：微信里不能放到主屏幕，复制也常被拦。请点右上角 <b>···</b> → <b>在默认浏览器中打开</b>（Safari），以后都从那边用。</div>' : ''}`;
}

function render() {
  setConn(offline ? '电脑不在线' : S.pc, !offline);
  drawTabs();
  if (view === 'wizard') { renderWizard(); return; }
  if (view === 'hist') { renderHist(); return; }
  if (view === 'set') { renderSet(); return; }
  const keepText = $('text') ? $('text').value : '';
  const focused = document.activeElement && document.activeElement.id === 'text';
  if (focused) { // 正在输入时只更新剪贴板卡片，不打断输入
    const box = document.querySelector('#clipBox'); if (box) box.innerHTML = clipCard(); bindClip(); return;
  }
  const moved = viaRelay && localStorage.getItem(K + 'moved') === '1';
  const autoTip = isIOS() && !inWeChat() && !S.auto && localStorage.getItem(K + 'tapTip') !== '1';
  $('app').innerHTML = `
    ${banners()}
    ${moved ? `<div class="card tipcard"><div class="ct">${icon('plus')}换成了在哪都能用的地址<button class="btn ghost small" id="movedX" style="margin-left:auto">知道了</button></div>以后手机用流量、在别的 Wi-Fi 也能打开。主屏幕上旧的“轻桥”图标只在家里的 Wi-Fi 能用，请到<b>设置</b>页重新放一次${isIOS() ? '；设过自动同步的，也在那里重新走一遍向导' : ''}。<div class="actions" style="margin-top:10px"><button class="btn soft" data-go="set">去设置页</button></div></div>` : ''}
    <div id="clipBox">${clipCard()}</div>
    ${sendCard()}
    ${inboxCard()}
    ${autoTip ? `<div class="card tipcard"><div class="ct">${icon('bolt')}不想每次都打开这个网页？<button class="btn ghost small" id="tapX" style="margin-left:auto">不用了</button></div>花 2 分钟设一次，以后<b>打开微信时</b>电脑刚复制的字已经在手机剪贴板里，手机复制的也会自己发到电脑。<div class="actions" style="margin-top:10px"><button class="btn primary" data-go="wizard">${icon('hand')}带我设置</button></div></div>` : ''}`;
  if ($('text')) $('text').value = keepText;
  bind();
}

function renderHist() {
  $('app').innerHTML = `${banners()}${histCard(true) || `<div class="card"><div class="ct">${icon('list')}电脑复制过的</div><div class="empty">电脑上复制过的文字、图片会留在这里，方便回头找。</div></div>`}${cloudCard()}`;
  bindHist();
  if ($('cloudPick')) $('cloudPick').onclick = () => $('cloudFiles').click();
  if ($('cloudFiles')) $('cloudFiles').onchange = () => { upload([...$('cloudFiles').files], true); $('cloudFiles').value = ''; };
}

function renderSet() {
  const ios = isIOS();
  const autoCard = ios ? `<div class="card">
      <div class="ct">${icon('bolt')}自动同步<span class="pill ${S.auto ? 'ok' : ''}">${S.auto ? '已设置' : '还没设置'}</span></div>
      <div class="setp">${S.auto ? `快捷指令“轻桥”上次运行：${when(S.auto)}。打开、离开你选的 App（或按你设的按钮）时就会两边同步文字。` : '设一次（约 2 分钟），以后<b>打开、离开微信这类 App 时自动</b>同步（也可以改成按按钮）：电脑刚复制的字进手机剪贴板，手机刚复制的发到电脑，不用打开这个网页。'}</div>
      <div class="actions"><button class="btn ${S.auto ? 'line' : 'primary'} big" data-go="wizard">${icon('hand')}${S.auto ? '重新设置 / 换了地址' : '带我一步步设置'}</button></div>
    </div>` : `<div class="card"><div class="ct">${icon('bolt')}自动同步</div><div class="setp">安卓手机：把这个网页放到主屏幕，打开后点一下屏幕就能拿到电脑刚复制的文字。</div></div>`;
  const home = standalone() ? '' : `<div class="card"><div class="ct">${icon('plus')}放到主屏幕</div><div class="setp">${homeHow()}以后像 App 一样一点就开，不用再扫码。</div></div>`;
  $('app').innerHTML = `${banners()}${autoCard}${home}
    <div class="card">
      <div class="ct">${icon('phone')}这部手机</div>
      <div class="kvrow"><span>名字</span><b>${esc(S.me)}</b></div>
      <div class="kvrow"><span>连着的电脑</span><b>${esc(S.pc)}</b></div>
      <div class="kvrow"><span>怎么连的</span><b>${viaRelay ? '经中转，在哪个网络都能用' : '同一个 Wi-Fi'}</b></div>
      <label class="check"><span class="switch"><input type="checkbox" id="quick" ${quickOn() ? 'checked' : ''}><span></span></span>电脑有新文字时，在“传送”页点一下屏幕任意处就复制</label>
      ${!viaRelay && S.relay ? '<div class="actions"><button class="btn soft" id="roam">换成离开这个 Wi-Fi 也能用的地址</button></div>' : ''}
      <div class="actions"><button class="btn line" id="forget" style="color:var(--bad)">断开这台电脑</button></div>
    </div>`;
  $('quick').onchange = e => localStorage.setItem(K + 'quick', e.target.checked ? '1' : '0');
  bindCommon();
}

function bindCommon() {
  document.querySelectorAll('[data-go]').forEach(b => b.onclick = () => { if (b.dataset.go === 'wizard') wzOpen(); else go(b.dataset.go); });
  if ($('roam')) $('roam').onclick = () => {
    if (!confirm('换成经中转的地址：手机用流量或在别的 Wi-Fi 也能用。换过去以后，请在新网页里重新“添加到主屏幕”。')) return;
    location.href = S.relay + '#t=' + encodeURIComponent(token);
  };
  if ($('forget')) $('forget').onclick = async () => {
    if (!confirm('断开后需要重新扫码才能再连。确定？')) return;
    try { await call('/api/phone/forget', {}); } catch { }
    localStorage.removeItem(K + 't'); token = ''; alive = false; $('tabbar').hidden = true; document.body.classList.remove('tabbed'); showJoin();
  };
}

// 电脑上新复制了文字：回到这个网页后点一下屏幕任意处就进手机剪贴板（iPhone 不让网页不经点按就写剪贴板，这是最省的一步）。
let copiedId = localStorage.getItem(K + 'copied') || '';
const quickOn = () => localStorage.getItem(K + 'quick') !== '0';
const quickReady = () => quickOn() && S && S.clip && S.clip.kind === 'text' && !S.clip.fromMe && S.clip.id !== copiedId;
function markCopied() { copiedId = S.clip.id; localStorage.setItem(K + 'copied', copiedId); }
document.addEventListener('click', e => {
  if (!quickReady() || view !== 'main') return;
  if (e.target.closest('button, a, input, textarea, select, label')) return;
  const ok = copyText(S.clip.text);
  if (ok) { markCopied(); render(); }
  toast(ok ? '已复制电脑上的文字，可以去粘贴了' : '复制没成功，点“复制到手机”再试', !ok);
});

// 看图：就在这一页上盖一层，右上角能关（以前是跳到图片地址，放在主屏幕用时没有返回键，只能把轻桥划掉重开）。
function openViewer(u) {
  closeViewer(true);
  const v = document.createElement('div'); v.className = 'viewer'; v.id = 'viewer';
  v.innerHTML = `<button class="vx" id="vx" aria-label="关闭">${icon('x')}</button>
    <div class="vbody"><span class="spin"></span><img src="${u}&view=1&thumb=2000" alt=""></div>
    <div class="vbar"><span>长按图片 → 存储到“照片”</span>${standalone() ? '' : `<a class="btn soft small" href="${u}" download>${icon('download')}下载原图</a>`}<button class="btn soft small" id="vclose">关闭</button></div>`;
  document.body.appendChild(v);
  const img = v.querySelector('img'); img.onload = () => v.classList.add('ready'); img.onerror = () => { v.querySelector('.vbody').innerHTML = '<div class="verr">图片打不开：电脑上的文件可能已经移走了。</div>'; };
  v.addEventListener('click', e => { if (e.target === img || e.target.closest('a')) return; closeViewer(); });
  try { history.pushState({ lbView: 1 }, ''); } catch { }
}
function closeViewer(silent) {
  const v = $('viewer'); if (!v) return;
  v.remove();
  if (!silent && history.state && history.state.lbView) try { history.back(); } catch { }
}
addEventListener('popstate', () => closeViewer(true));
document.addEventListener('click', e => { const t = e.target.closest('[data-view]'); if (t) { e.preventDefault(); openViewer(t.dataset.view); } });

function bindClip() {
  const c0 = $('copy'); if (c0) c0.addEventListener('click', () => { if (S.clip && S.clip.kind === 'text') { markCopied(); setTimeout(render, 50); } });
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
  if ($('movedX')) $('movedX').onclick = () => { localStorage.removeItem(K + 'moved'); render(); };
  if ($('tapX')) $('tapX').onclick = () => { localStorage.setItem(K + 'tapTip', '1'); render(); };
  if ($('readClip')) $('readClip').onclick = async () => {
    try {
      const t = await navigator.clipboard.readText();
      if (t && t.trim()) { sendText(t); return; }
    } catch { }
    // 剪贴板里是图片，或者浏览器没让读：退回粘贴
    $('text').focus(); toast('长按框里 → 粘贴', true);
  };
  bindCommon();
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

// ───────────── iPhone 自动同步向导（快捷指令 + 轻点背面） ─────────────
// 苹果不让网页替用户装快捷指令（要 iCloud 链接或 Mac 签名），所以做成一步一屏：
// 每步一个大按钮直接跳到该去的 App，回来后网页自己看电脑收没收到，收到了才打勾。

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
  return { url: location.origin + '/', how: '用的是电脑现在的 IP。电脑换了网络后要回来重新设置' };
}

// 走到第几步、进这一步时电脑记的“上次运行时间”：跳去别的 App 再回来（网页可能被系统重新加载）还接着走。
let wz = (() => { try { return JSON.parse(localStorage.getItem(K + 'wz')) || null; } catch { return null; } })();
const WZ = ['开始', '建快捷指令', '试一下', '怎么运行'];
// 平时怎么运行“轻桥”：[键, 名字, 一句话, 步骤, 等待提示]
const wzTrig = () => localStorage.getItem(K + 'trig') || 'auto';
const TRIGS = [
  ['auto', '全自动（推荐）', '打开、离开微信等 App 时自己运行，什么都不用按', `<a class="btn soft big wzjump" href="shortcuts://">${icon('open')}打开“快捷指令”</a>
    <ol class="wzlist">
      <li>底部点 <b>自动化</b> → 右上角 <b>+</b>（第一次是“新建自动化”）</li>
      <li>往下找到 <b>App</b> → 点<b>选取</b>，勾上常用的（微信、备忘录、Safari……）→ 完成</li>
      <li><b>已打开</b>、<b>已关闭</b>都勾上 → 选<b>立即运行</b> → 下一步</li>
      <li>在列表里点 <b>轻桥</b>（不要点“新建空白自动化”）</li>
    </ol>
    <div class="hint">效果：在电脑上复制，打开微信就能直接粘贴；在微信里复制，切走时就发到电脑。每次都问“允许粘贴”的话：iPhone 设置 → App → 快捷指令 → 从其他 App 粘贴 → 允许。</div>`, '设好后打开一次刚才勾的 App，再回到这里…'],
  ['action', '侧边的操作按钮', 'iPhone 15 Pro 及以后，左上角那个小按钮按住一下', `<div class="wzpath"><span>设置</span><span>操作按钮</span><span>左右滑到“快捷指令”</span><span class="on">轻桥</span></div>
    <div class="hint">按住操作按钮一下就运行。没有这个按钮的 iPhone 选别的方式。</div>`, '设好后按住操作按钮一下，再回到这里…'],
  ['cc', '控制中心按钮', '从右上角往下拉，点一下“轻桥”', `<ol class="wzlist">
      <li>从屏幕<b>右上角往下拉</b>出控制中心 → 点左上角 <b>+</b> → <b>添加控制</b></li>
      <li>搜 <kbd>快捷指令</kbd> → 点它 → 选 <b>轻桥</b> → 点空白处完成</li>
    </ol>
    <div class="hint">锁屏底部的两个按钮也能换成它：长按锁屏 → 自定 → 锁定屏幕 → 点底部按钮 → 快捷指令 → 轻桥。（iOS 18 及以上）</div>`, '设好后在控制中心点一下“轻桥”，再回到这里…'],
  ['back', '轻点背面', '手机背面敲两下或三下', `<div class="wzpath"><span>设置</span><span>辅助功能</span><span>触控</span><span>轻点背面</span><span>轻点两下（或三下）</span><span class="on">轻桥</span></div>
    <div class="hint">“轻点背面”在“触控”页最下面；“轻桥”在列表往下翻的“快捷指令”一栏里。戴厚壳的敲重一点。</div>`, '设好后在手机背面敲一下试试，再回到这里…'],
];
function wzSave() { localStorage.setItem(K + 'wz', JSON.stringify(wz)); }
const badAt = () => (S.bad && S.bad.at) || 0;
function wzOpen() { if (!wz || wz.i >= WZ.length) wz = { i: 0, base: S.auto || 0, bbase: badAt() }; wzSave(); go('wizard'); }
function wzGo(i) { wz = { i, base: S.auto || 0, bbase: badAt() }; wzSave(); render(); scrollTo(0, 0); }
function wzClose() { localStorage.removeItem(K + 'wz'); wz = null; go('set'); }
// 进这一步之后快捷指令又运行过 = 电脑收到了
const wzHit = () => !!(wz && S.auto && S.auto !== wz.base);
// 进这一步之后快捷指令到了电脑、但配错了：直接说改哪里
const wzBad = () => (wz && S.bad && S.bad.at > (wz.bbase || 0)) ? S.bad.kind : null;
const BAD = {
  get: '<b>电脑收到了，但“方法”还是 GET。</b>回到快捷指令，在“获取 URL 的内容”那一条点<b>“内容”右边的 ⌄</b> → <b>方法</b>改成 <b>POST</b> → <b>请求体</b>选<b>文件</b> → <b>文件</b>选<b>剪贴板</b>，再点一次上面的按钮。',
  urlvar: '<b>电脑收到了，但“URL”那一格是蓝色的“剪贴板”，不是地址</b>（刚好剪贴板里是地址才连上）。点那个蓝色小块 → 清除变量 → 再点一下这一格 → 点<b>黑色小菜单</b>里的<b>粘贴</b>，不要点键盘上方那排里的“剪贴板”。改完是黑色普通文字。',
  body: '<b>电脑收到了，但“请求体”选的是 JSON 或表单。</b>改成<b>文件</b>，下面的<b>文件</b>选<b>剪贴板</b>，再点一次上面的按钮。',
};

function wzCheck(waiting, done) {
  if (wzHit()) return `<div class="wzok">${icon('check')}<div><b>电脑收到了</b>${done}</div></div>`;
  const bad = wzBad();
  if (bad) return `<div class="wzbad">${icon('info')}<div>${BAD[bad] || BAD.get}</div></div>`;
  return `<div class="wzwait"><span class="spin"></span><div>${waiting}</div></div>`;
}

function renderWizard() {
  if (!wz) wz = { i: 0, base: S.auto || 0 };
  const host = guideHost || { url: viaRelay ? location.origin + BASE : location.origin + '/', how: '正在检查哪个地址能用…' };
  // 授权放在地址里，快捷指令就不用另外加“头部”。
  const url = host.url + 'v1/swap?t=' + encodeURIComponent(token);
  const i = wz.i, last = WZ.length - 1;
  let body, next = '下一步', nextOk = true, skip = '';
  if (i === 0) {
    body = `<h2>设一次，以后不用打开网页</h2>
      <div class="wzflow">
        <div><span>${icon('laptop')}</span>电脑上复制<em>→</em><span>${icon('bolt')}</span>手机上运行一下“轻桥”<em>→</em><span>${icon('phone')}</span>手机上粘贴</div>
        <div><span>${icon('phone')}</span>手机上复制<em>→</em><span>${icon('bolt')}</span>手机上运行一下“轻桥”<em>→</em><span>${icon('laptop')}</span>电脑上 Ctrl+V</div>
      </div>
      <p class="wzp">“运行一下”可以是<b>全自动</b>（打开、离开微信这类 App 时自己运行），也可以是按侧边按钮、控制中心、轻点背面，最后一步你自己挑。</p>
      <p class="wzp">靠的是 iPhone 自带的<b>“快捷指令”</b>App。苹果不让网页替你自动装，所以要照着点几下——<b>每一步都有按钮带你跳过去，做完回到这里，我会告诉你成没成</b>。大约 2 分钟。</p>
      ${inWeChat() ? '<div class="banner">请先换到 Safari 里打开再设置（微信里跳不到快捷指令）。</div>' : ''}`;
    next = '开始';
  } else if (i === 1) {
    body = `<h2>第 1 步 · 新建快捷指令</h2>
      <a class="btn primary big wzjump" id="wzMake" href="shortcuts://create-shortcut">${icon('copy')}复制地址，并打开“快捷指令”</a>
      <div class="hint" style="text-align:center">${esc(host.how)}。地址里带着这部手机的授权，别发给别人。</div>
      <p class="wzp">跳过去以后是一张空白的新快捷指令。<b>只用底部的搜索框</b>（上面推荐的那些不要点），<b>按顺序</b>加 3 个操作：</p>
      <ol class="wzlist">
        <li>搜 <kbd>URL</kbd> → 点名字<b>只有“URL”两个字</b>的那个 → 点里面浅色的“URL”，键盘弹出来后<b>再点一下</b>这一格（或长按）→ 点<b>黑色小菜单</b>里的<b>粘贴</b>
          <div class="wzsub"><b>不要点键盘上方那一排里的“剪贴板”</b>——那个会放进一个<b>蓝色小块</b>（剪贴板变量），看着像地址，其实不是。粘贴对了是<b>黑色的普通文字</b> http://…/v1/swap?t=…。<br>已经是蓝色小块的：点它 → 清除变量，再按上面的办法粘贴。</div>
          <div class="wzsub">黑色小菜单不出来：光标在这一格里时，用<b>三根手指向外张开</b>，也是粘贴。</div></li>
        <li>搜 <kbd>URL 内容</kbd> → 点 <b>获取 URL 内容</b>，它自动写成“获取 <u class="pillv">URL</u> 的内容”，不用改。点“内容”右边的 <b>⌄</b> 展开：
          <div class="wzsub"><b>方法</b>选 <b>POST</b> → <b>请求体</b>选<b>文件</b> → 点“文件”那一行 → 在弹出的选项里点<b>剪贴板</b>。<br>这个“剪贴板”是 iPhone 自带的，<b>不用</b>另外加“获取剪贴板”操作。</div></li>
        <li>搜 <kbd>拷贝</kbd> → 点 <b>拷贝至剪贴板</b>（自动写成“将 URL的内容 拷贝至剪贴板”，不用改）</li>
        <li>点最上面的名字 → <b>重新命名</b> → 改成 <kbd>轻桥</kbd> → 右上角<b>完成</b>
          <div class="wzsub">以前建过的都删掉，只留这一条叫“轻桥”的。</div></li>
      </ol>
      <div class="wzmockt">做完应该长这样（就这 3 个）：</div>
      <div class="wzmock">
        <div class="mk"><i class="mi b">${icon('link')}</i><div>URL<small class="plain">${esc(url.replace(/^https?:\/\//, '').slice(0, 34))}…</small></div></div>
        <div class="mk"><i class="mi g">${icon('download')}</i><div>获取 <u>URL</u> 的内容<small>方法 <b>POST</b>　请求体 <b>文件</b>　文件 <u>剪贴板</u></small></div></div>
        <div class="mk"><i class="mi y">${icon('copy')}</i>将 <u>URL的内容</u> 拷贝至剪贴板</div>
      </div>
      <details class="wzfold"><summary>已经建过、想改旧的</summary><div class="hint">
        ① 删掉最上面的“获取剪贴板”（点它右边的 ×）。② “URL”那一格如果是蓝色小块：点它 → 清除变量 → 粘贴地址。③ “获取 URL 的内容”展开，方法 POST、请求体 文件、文件 剪贴板。</div></details>
      <details class="wzfold"><summary>没跳过去 / 想自己复制地址</summary><div class="kv"><code>${esc(url)}</code><button class="btn soft small" data-copy="${esc(url)}">${icon('copy')}复制</button></div><div class="hint">手动打开“快捷指令”App，点右上角 <b>+</b>，再照上面做。找不到这个 App 就去 App Store 搜“快捷指令”装回来（苹果自己出的，免费）。</div></details>`;
    next = '建好了，去试一下';
  } else if (i === 2) {
    body = `<h2>第 2 步 · 试一下</h2>
      <p class="wzp">点下面的按钮运行一次。第一次会问<b>“允许‘轻桥’连接到…”</b>和<b>“允许粘贴”</b>，都点<b>允许</b>。运行完回到这个网页。</p>
      <a class="btn primary big wzjump" href="shortcuts://run-shortcut?name=${encodeURIComponent('轻桥')}">${icon('play')}运行“轻桥”</a>
      ${wzCheck('等电脑收到…（运行完回到这里）', '，快捷指令没问题。')}
      ${wzHit() ? '' : `<details class="wzfold" ${wzBad() ? '' : 'open'}><summary>没打勾？看弹出的是哪句</summary><div class="hint">
        · <b>无效的 URL</b> 或 <b>无法将文本转换为 URL</b>：“URL”那一格是蓝色小块（剪贴板），不是地址。删掉“获取剪贴板”操作，点蓝色小块 → 清除变量 → 粘贴地址。<br>
        · 拷回来的是 <b>“轻桥：快捷指令还差一步…”</b>：照那句话改方法 / 请求体。<br>
        · 提示<b>找不到快捷指令</b>：名字要一字不差叫 <b>轻桥</b>。<br>
        · 提示<b>网络连接已中断</b>：再运行一次；${viaRelay || S.relay ? '还不行就看看手机是不是开着代理 / 加速器，先关掉再试。' : '手机和电脑要在同一个 Wi-Fi。'}</div></details>`}`;
    nextOk = wzHit(); skip = '先跳过';
  } else {
    const trig = TRIGS.find(t => t[0] === wzTrig()) || TRIGS[0];
    body = `<h2>第 3 步 · 平时怎么运行它</h2>
      <p class="wzp">挑一种顺手的（可以以后回来换，也可以都设）：</p>
      <div class="trigs">${TRIGS.map(t => `<button class="trig ${t === trig ? 'on' : ''}" data-trig="${t[0]}"><b>${t[1]}</b><small>${t[2]}</small></button>`).join('')}</div>
      <div class="trigbody">${trig[3]}</div>
      ${wzCheck(trig[4], '，以后就这样用。')}`;
    next = '完成'; nextOk = wzHit(); skip = '先完成，以后再试';
  }
  const html = `${banners()}
    <div class="wzhead"><button class="back" id="wzBack">${icon('back')}${i === 0 ? '返回' : '上一步'}</button><div class="wzdots">${WZ.map((_, k) => `<i class="${k < i ? 'done' : k === i ? 'on' : ''}"></i>`).join('')}</div><button class="back" id="wzQuit">退出向导</button></div>
    <div class="card wz">${body}
      <div class="actions wznav"><button class="btn primary big" id="wzNext" ${nextOk ? '' : 'disabled'}>${next}</button></div>
      ${skip && !nextOk ? `<button class="wzskip" id="wzSkip">${skip}</button>` : ''}
    </div>`;
  // 状态没变就不重画：免得把用户展开的“没成功怎么办”又收回去
  if (renderWizard.last === html && $('wzNext')) return;
  renderWizard.last = html; $('app').innerHTML = html;
  $('wzBack').onclick = () => i === 0 ? wzClose() : wzGo(i - 1);
  $('wzQuit').onclick = wzClose;
  document.querySelectorAll('[data-trig]').forEach(b => b.onclick = () => { localStorage.setItem(K + 'trig', b.dataset.trig); renderWizard.last = null; renderWizard(); });
  $('wzNext').onclick = () => { if (i === last) { toast('设置好了'); wzClose(); } else wzGo(i + 1); };
  if ($('wzSkip')) $('wzSkip').onclick = () => wzGo(i + 1);
  // 先把地址放进手机剪贴板（这一下点按就是苹果要的“用户操作”），再让链接跳去快捷指令。
  if ($('wzMake')) $('wzMake').addEventListener('click', () => { toast(copyText(url) ? '地址已复制，正在打开快捷指令…' : '没复制上，展开下面手动复制', false); });
  document.querySelectorAll('[data-copy]').forEach(b => b.onclick = () => toast(copyText(b.dataset.copy) ? '已复制' : '复制没成功，长按手动复制', false));
  if (!guideHost && !renderWizard.busy) { renderWizard.busy = true; pickHost().then(h => { guideHost = h; renderWizard.busy = false; if (view === 'wizard') renderWizard(); }); }
}

boot();
