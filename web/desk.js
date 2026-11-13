// 轻桥 2.0 · 电脑界面
const hash = new URLSearchParams(location.hash.slice(1));
const TOKEN = hash.get('t') || sessionStorage.getItem('lbt') || '';
if (hash.get('t')) { sessionStorage.setItem('lbt', TOKEN); history.replaceState(null, '', '/desk'); }
const $ = id => document.getElementById(id);
const hasWebView = !!(window.chrome && chrome.webview && chrome.webview.postMessageWithAdditionalObjects);

let S = null, rev = 0;
let remoteCodeAsked = false;
let openMenu = null, settingsOpen = false, addOpen = false, addTab = 'phone', editingName = false, qrIp = null, qrLan = false, remoteOpen = false;

async function api(path, body) {
  const r = await fetch('/api/local/' + path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { 'X-LB-Local': TOKEN, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || ('出错了（' + r.status + '）'));
  return j;
}
async function act(path, body, ok) {
  try { const r = await api(path, body); if (ok) toast(ok); return r; }
  catch (e) { toast(e.message, true); return null; }
}

async function loop() {
  for (;;) {
    try {
      const r = await fetch('/api/local/state?rev=' + rev, { headers: { 'X-LB-Local': TOKEN } });
      if (r.status === 401) { document.body.innerHTML = '<div style="padding:60px;text-align:center;color:#888">请从轻桥窗口打开</div>'; return; }
      const s = await r.json(); S = s; rev = s.rev; render();
    } catch { await sleep(1500); }
  }
}

function set(el, html) { if (el._h !== html) { el._h = html; el.innerHTML = html; } }
function devIcon(d) { return d.kind === 'phone' || d.form === 'phone' ? 'phone' : d.form === 'laptop' ? 'laptop' : 'desktop'; }
const isMe = n => S && n === S.me.name;
const tok = () => 't=' + encodeURIComponent(TOKEN);

// ───────────── 渲染 ─────────────

// 两页：主页只放设备和最近几条，动态放全部记录
let page = (() => { try { return localStorage.getItem('lb.page') || 'home'; } catch { return 'home'; } })();
function setPage(p) { page = p; try { localStorage.setItem('lb.page', p); } catch { } if (p === 'feed') markSeen(); render(); scrollTo(0, 0); }
const seenAt = () => { try { return +localStorage.getItem('lb.feedSeen') || 0; } catch { return 0; } };
function markSeen() { const t = S && S.feed.length ? Math.max(...S.feed.map(f => f.time || 0)) : Date.now(); try { localStorage.setItem('lb.feedSeen', String(t)); } catch { } }
document.addEventListener('click', e => { const b = e.target.closest('[data-page]'); if (b) setPage(b.dataset.page); });
function renderPages() {
  document.body.classList.toggle('page-home', page === 'home');
  document.body.classList.toggle('page-feed', page === 'feed');
  document.querySelectorAll('#dtabs [data-page]').forEach(b => b.classList.toggle('on', b.dataset.page === page));
  if (page === 'feed') markSeen();
  const fresh = S.feed.filter(f => f.kind !== 'info' && (f.time || 0) > seenAt()).length;
  const badge = $('feedBadge'); badge.classList.toggle('hide', !fresh || page === 'feed'); badge.textContent = fresh > 99 ? '99+' : fresh;
  if (page !== 'home') return;
  const items = S.feed.filter(f => f.kind !== 'info').slice(0, 3);
  set($('recentWrap'), `<div class="sec-head"><h2>最近</h2><div class="grow"></div><button class="btn ghost small" data-page="feed">全部动态 ›</button></div>
    ${items.length ? `<ol class="recent">${items.map(f => `<li data-page="feed">${icon(f.kind === 'text' ? 'text' : f.kind === 'image' ? 'image' : 'file')}<span class="rt">${esc(f.kind === 'text' ? (f.text || '').replace(/\s+/g, ' ').slice(0, 80) : (f.files || f.items || []).map(x => x.name).join('、') || '文件')}</span><span class="rm">${esc(f.from || '')} · ${hhmm(f.time)}</span></li>`).join('')}</ol>` : '<div class="recent-empty">复制或发送的东西会出现在这里</div>'}`);
}

function render() {
  if (!S) return;
  renderPages();
  renderTop(); renderRequests(); renderPending();
  const none = S.devices.length === 0 && S.phones.length === 0;
  $('welcome').classList.toggle('hide', !none);
  $('devicesWrap').classList.toggle('hide', none);
  if (none) set($('welcome'), welcomeHtml()); else renderDevices();
  renderNearby(none);
  renderFeed();
  if (settingsOpen) renderSettings();
  if (addOpen) renderAdd();
  $('histBtn').classList.toggle('hide', !S.history.count);
  if (addOpen && remoteOpen && S.relay.url && S.relay.state === 'ok' && !S.relay.code && !remoteCodeAsked) { remoteCodeAsked = true; act('remote-code', {}); }
  if (histOpen && S.history.rev !== histRev) loadHistory();
  if (cloudOpen && (S.cloud.rev !== cloudRev || S.cloud.ready !== cloudReady)) loadCloud();
  if (S.pair.until - Date.now() < 60000 && !render.refreshing) { render.refreshing = true; act('pair-refresh', {}).finally(() => render.refreshing = false); }
}

function renderTop() {
  if (!editingName) set($('me'), `<span class="me-label">本机</span><b>${esc(S.me.name)}</b>${icon('edit', 'muted')}`);
  const p = S.settings.paused;
  const online = S.devices.filter(d => d.state !== 'offline').length + S.phones.filter(d => d.state !== 'offline').length;
  const relayOk = S.relay && S.relay.url && S.relay.state === 'ok';
  const parts = [];
  if (online) parts.push('自动同步中');
  if (relayOk) parts.push('跨网络已开');
  if (S.cloud && S.cloud.ready) parts.push('小云可用');
  if (!parts.length) parts.push('等其他设备上线');
  $('sync').className = 'sync' + (p ? ' paused' : '');
  set($('sync'), p ? `${icon('pause')}同步已暂停` : `<i class="dot ${online || relayOk ? 'on' : ''}"></i>${parts.join(' · ')}`);
  $('sync').title = p ? '点一下恢复' : '点一下暂停全部同步';
  set($('openSettings'), icon('gear'));
}

function renderRequests() {
  set($('requests'), S.requests.map(r => `
    <div class="request">
      <div class="ric">${icon(r.kind === 'phone' ? 'phone' : devIcon(r))}</div>
      <div class="grow">
        <div class="rt">“${esc(r.name)}”想连接这台电脑</div>
        <div class="rs">${r.kind === 'phone' ? '手机上显示的核对码' : '那台电脑上显示的核对码'}：<span class="code">${esc(r.code)}</span> · 一样就点允许。允许后它会自动连上，以后不用再点。</div>
      </div>
      <button class="btn ghost" data-answer="${r.id}" data-allow="0">拒绝</button>
      <button class="btn primary" data-answer="${r.id}" data-allow="1">${icon('check')}允许</button>
    </div>`).join(''));
}

function renderPending() {
  const p = S.pending;
  if (!p) { set($('pending'), ''); return; }
  const targets = [...S.devices, ...S.phones];
  set($('pending'), `
    <div class="modal"><div class="box">
      <div class="mh">${icon('send')}<h2>把 ${p.count === 1 ? '“' + esc(p.names[0]) + '”' : p.count + ' 个文件'} 发到哪里？</h2><div class="grow"></div><button class="iconbtn" data-cancel-pending>${icon('x')}</button></div>
      ${targets.length ? `<div class="targets">${targets.map(d => `<button class="target" data-send-pending="${d.id}">${icon(devIcon(d))}${esc(d.name)}<small>${d.state === 'offline' ? (d.kind === 'phone' ? '放进它的收件箱' : '上线后送达') : '在线'}</small></button>`).join('')}</div>` : '<p class="muted" style="margin:6px">还没有连接其他设备。先在主界面添加设备。</p>'}
    </div></div>`);
}

function devSub(d) {
  if (d.kind === 'phone') return d.state === 'online' ? '手机上正开着轻桥' : d.auto ? '快捷指令同步 · 上次 ' + when(Math.max(d.auto, d.seen || 0)) : '已配对 · ' + (d.seen ? '上次打开 ' + when(d.seen) : '还没打开过');
  if (d.state === 'online') return d.paused ? '对方暂停了同步' : d.route === 'relay' ? '在线 · 不在同一个网络（经中转）' : '在线 · ' + (d.addr || '');
  if (d.state === 'reverse') return '在线（只能由它连过来）';
  if (d.error) return d.error;
  return d.seen ? '离线 · ' + when(d.seen) : '等它上线';
}

function renderDevices() {
  // 在线的排前面；同一类里按最近在线
  const all = [...S.devices, ...S.phones].sort((a, b) => (a.state === 'offline') - (b.state === 'offline') || (b.seen || 0) - (a.seen || 0));
  set($('devices'), all.map(d => {
    const on = d.state !== 'offline';
    return `<div class="dev ${on ? 'online' : ''}" data-dropdev="${d.id}">
      <button class="more" data-menu="${d.id}" title="更多">${icon('more')}</button>
      <div class="dic">${icon(devIcon(d))}<i class="dot ${on ? 'on' : ''}"></i></div>
      <div class="dname" title="${esc(d.name)}">${esc(d.name)}</div>
      <div class="dsub ${d.error && !on ? 'warn' : ''}" title="${esc(devSub(d))}">${esc(devSub(d))}</div>
      <div class="dact"><button class="btn soft small" data-pick="${d.id}">${icon('send')}发文件</button>${d.pending ? `<span class="muted" style="font-size:12px;align-self:center">${d.pending} 项排队中</span>` : ''}</div>
      ${openMenu === d.id ? `<div class="menu">
        <button data-pick="${d.id}">${icon('file')}发文件</button>
        <button data-pick-folder="${d.id}">${icon('folder')}发文件夹</button>
        ${d.kind === 'phone' ? `<button data-open-history="${d.id.replace('phone:', '')}">${icon('text')}发来的内容</button>` : ''}
        <button class="bad" data-remove="${d.id}">${icon('trash')}${d.kind === 'phone' ? '移除这部手机' : '移除这台电脑'}</button>
      </div>` : ''}
    </div>`;
  }).join('') + cloudCard());
  $('devHint').textContent = all.length ? '把文件拖到设备上就能发过去' : '';
  const fresh = (S.nearby || []).length;
  set($('addBtn'), `${icon('plus')}添加设备${fresh ? `<span class="badge">${fresh}</span>` : ''}`);
}

function nearHtml(n) {
  let right, sub = esc(n.ip);
  if (n.state === 'asking') right = `<span class="spin"></span><span class="muted">正在请求…</span>`;
  else if (n.state === 'waiting') { sub = `请在“${esc(n.name)}”上点允许 · 核对码 <span class="code">${esc(n.code)}</span>`; right = `<span class="spin"></span><button class="btn ghost small" data-cancel="${n.id}">取消</button>`; }
  else if (n.state === 'rejected') { sub = '对方点了拒绝'; right = `<button class="btn line small" data-connect="${n.id}">再试一次</button>`; }
  else if (n.state === 'failed') { sub = `<span class="bad">${esc(n.error || '没连上')}</span>`; right = `<button class="btn line small" data-connect="${n.id}">再试一次</button>`; }
  else right = `<button class="btn primary small" data-connect="${n.id}">${icon('link')}连接</button>`;
  return `<div class="near"><div class="nic">${icon(devIcon(n))}</div><div class="grow"><div class="nn">${esc(n.name)}</div><div class="ns">${sub}</div></div>${right}</div>`;
}

function renderNearby(inWelcome) {
  const show = !inWelcome && S.nearby.length > 0;
  $('nearbyWrap').classList.toggle('hide', !show);
  if (show) set($('nearby'), S.nearby.map(nearHtml).join(''));
}

function phonePanel() {
  const ip = qrIp && S.me.addrs.some(a => a.ip === qrIp) ? qrIp : S.me.ip;
  const url = `http://${ip}:${S.me.port}`;
  const others = S.me.addrs.filter(a => a.ip !== ip);
  const relay = S.pair.relayUrl && !qrLan;
  return `<div class="panel">
    <h3>${icon('phone')}连手机</h3>
    <p>iPhone、安卓都行，不用装 App。${relay ? '手机用流量、连别的 Wi-Fi 也能用。' : '手机和电脑连同一个 Wi-Fi。'}</p>
    <div class="qrbox">
      <div class="qr"><img alt="二维码" src="/api/local/qr?ip=${encodeURIComponent(ip)}&mode=${relay ? 'relay' : 'lan'}&v=${S.pair.until}&${tok()}"></div>
      <ol class="steps">
        <li>打开手机相机，对准二维码</li>
        <li>点弹出的链接，就连上了</li>
        <li>网页底部点<b>设置</b>：放到主屏幕；iPhone 再跟着向导设一次，以后打开微信这类 App 时<b>自动同步</b>，不用打开网页</li>
      </ol>
    </div>
    ${S.pair.relayUrl ? `<div class="qrmode">${relay ? `经中转，在哪都能用 · <button class="linkbtn" data-qrlan="1">只在同一个 Wi-Fi 用（更快）</button>` : `只在同一个 Wi-Fi 用 · <button class="linkbtn" data-qrlan="0">改成在哪都能用</button>`}</div>` : ''}
    <details class="fold"><summary>扫码打不开？</summary><div class="body">
      ${relay ? `<div>在手机浏览器里输入 <span class="addr">${esc(S.pair.relayUrl)}</span>，然后在这台电脑上点“允许”。</div>` : `<div>在手机浏览器里输入 <span class="addr">${esc(url)}</span>，然后在这台电脑上点“允许”。</div>
      ${others.length ? `<div>电脑有好几个网络地址，换一个试试：${others.map(a => `<button class="btn line small" data-qrip="${a.ip}">${a.ip}</button>`).join(' ')}</div>` : ''}
      <div>手机连的如果是“访客 Wi-Fi”，通常连不到电脑，换成主 Wi-Fi。</div>`}
    </div></details>
  </div>`;
}

function fmtCode(c) { return c ? c.slice(0, 4) + '-' + c.slice(4) : ''; }

// 正在找：只在刚打开“添加设备”或点了“再找一次”后转（后台的定期查找不转圈）。
let scanAskedAt = 0;
function searchingHtml() {
  const busy = S.scanning && Date.now() - scanAskedAt < 20000;
  return `<div class="searching">${busy ? '<span class="spin"></span>正在找同一网络里的轻桥…' : `${icon('desktop', 'muted')}<span>同一网络里还没找到别的轻桥${S.lastScan ? ' · ' + when(S.lastScan) + '找过' : ''}</span>`}<div class="grow"></div><button class="btn ghost small" data-scan>${icon('refresh')}再找一次</button></div>`;
}

function tempStatus(t) {
  if (t.state === 'download') return `<span class="spin"></span><span>正在下载 Cloudflare 隧道程序 ${t.progress || 0}%（只下载一次）</span>`;
  if (t.state === 'starting' || (t.on && t.state === 'off')) return '<span class="spin"></span><span>正在打开临时隧道…</span>';
  if (t.state === 'error') return `<span class="bad">${esc(t.error || '临时隧道没开起来')}</span>`;
  return '';
}

function remoteFold() {
  const r = S.relay, t = r.temp;
  let body;
  if (t.on && (t.state !== 'ok' || !r.url)) body = `<div class="relayoff">
      <div><b>临时中转</b>：这台电脑当中转，经 Cloudflare 临时隧道连到外网。</div>
      <div class="tstate">${tempStatus(t)}</div>
      <div class="actions">${t.state === 'error' ? `<button class="btn primary small" data-temp-retry>${icon('refresh')}再试一次</button>` : ''}<button class="linkbtn" data-relay-off>${t.state === 'error' ? '不用了' : '取消'}</button></div>
    </div>`;
  else if (!r.url) body = `<div class="relayoff">
      <div><b>开启跨网络</b>后，家里和公司的电脑、用流量的手机都能互传，不用装别的东西。</div>
      <div class="actions"><button class="btn primary small" data-relay-enable>${icon('link')}开启跨网络</button><button class="linkbtn" data-open-settings="relay">用自己的服务器</button></div>
    </div>`;
  else {
    const warn = r.state === 'error' ? `<div class="bad">中转暂时连不上（${esc(r.error || '网络问题')}），连上后才能配对。</div>` : '';
    const code = r.codeText || fmtCode(r.code);
    body = `${warn}
      <div>1. 在另一台电脑的轻桥里输入本机的连接码：</div>
      <div class="paircode">${code ? `<span class="code ${code.includes('@') ? 'long' : ''}">${esc(code)}</span><button class="btn ghost small" data-copy-code="${esc(code)}">${icon('copy')}复制</button><span class="muted">10 分钟内有效</span>` : r.state === 'ok' ? '<span class="spin"></span><span class="muted">正在生成…</span>' : '<span class="muted">连上中转后显示</span>'}</div>
      ${r.mode === 'temp' ? '<div class="muted">用的是临时中转：这台电脑重启后地址会变，那时别处的电脑要再输入一次新的连接码（不用重新核对）。</div>' : ''}
      <div>2. 或者输入那台电脑显示的连接码：</div>
      <div class="manual"><input class="input" id="remoteCode" placeholder="例如 K7M2-PQ9X（带 @ 的整个粘贴进来）" maxlength="120" autocomplete="off" spellcheck="false"><button class="btn primary small" data-remote-connect>连接</button></div>
      <div>输入后，到对方电脑上核对数字、点“允许”。以后两台电脑不管在哪个网络都会自动连上。</div>`;
  }
  return `<details class="fold" data-remote ${remoteOpen ? 'open' : ''}><summary>不在同一个网络？（比如家里和公司）</summary><div class="body">${body}</div></details>`;
}

function pcPanel() {
  const url = `http://${S.me.ip}:${S.me.port}`;
  return `<div class="panel">
    <h3>${icon(S.me.form === 'laptop' ? 'laptop' : 'desktop')}连另一台电脑</h3>
    <p>在那台电脑上也打开轻桥，它会自动出现在下面，点“连接”，再到那台电脑上点“允许”。</p>
    <div class="nearby">${S.nearby.length ? S.nearby.map(nearHtml).join('') : searchingHtml()}</div>
    ${remoteFold()}
    <details class="fold"><summary>那台电脑还没有轻桥？</summary><div class="body">
      <div>在那台电脑的浏览器里打开 <span class="addr">${esc(url)}/get</span>，下载后双击就能用，不用安装。</div>
    </div></details>
    <details class="fold"><summary>一直找不到？</summary><div class="body">
      <div>1. 第一次打开轻桥时，Windows 会问“是否允许访问网络”，两台都要点“允许”。点错了：<button class="btn line small" data-firewall>${icon('shield')}允许轻桥通过防火墙</button></div>
      <div>2. 手动输入那台电脑的地址（在它的轻桥“设置 → 网络”里能看到）：</div>
      <div class="manual"><input class="input" id="manualAddr" placeholder="例如 192.168.1.8"><button class="btn primary small" data-manual>连接</button></div>
    </div></details>
  </div>`;
}

function welcomeHtml() {
  return `<div class="hero"><h1>把这台电脑和你的其他设备连起来</h1><p>连一次就行。以后复制的文字、截图、文件，会自动出现在其他设备上，直接粘贴。</p></div>
    <div class="welcome" style="margin-top:16px">${phonePanel()}${pcPanel()}</div>`;
}

function renderAdd() {
  set($('addModal'), `<div class="box">
    <div class="mh">${icon('plus')}<h2>添加设备</h2><div class="grow"></div><button class="iconbtn" data-close-add>${icon('x')}</button></div>
    <div class="welcome">${phonePanel()}${pcPanel()}</div>
  </div>`);
}

function feedTitle(f) {
  if (f.kind === 'info') return esc(f.note || '');
  if (f.kind === 'text') return '文字' + (f.chars > 0 ? ` <span class="muted">· ${f.chars} 字</span>` : '');
  if (f.kind === 'image') return f.files.length ? esc(f.files[0].name) : '图片';
  const n = f.files.length + (f.more || 0);
  if (n === 1) return esc(f.files[0].name);
  return n ? `${n} 个文件` : (f.kind === 'send' ? '文件' : '复制的文件');
}
function feedStatus(f) {
  if (f.kind === 'info') return '';
  const pct = f.total > 0 ? Math.min(100, Math.round(f.done * 100 / f.total)) : 0;
  if (f.status === 'sending') return `<span class="status wait">${f.dir === 'out' && f.total ? '发送中 ' + pct + '%' : '发送中'}</span>`;
  if (f.status === 'receiving') return `<span class="status wait">接收中 ${pct}%</span>`;
  if (f.status === 'failed') return `<span class="status bad">${icon('x')}没成功</span>`;
  if (f.status === 'waiting') return `<span class="status wait">等手机下载</span>`;
  if (f.status === 'done') return `<span class="status ok">${icon('check')}${f.dir === 'out' ? (f.clip ? '已同步' : '已送达') : (f.clip ? '已同步' : '已收到')}</span>`;
  return '';
}
function route(f) {
  const to = (f.to || []).map(n => isMe(n) ? '本机' : n);
  const from = isMe(f.from) ? '本机' : f.from;
  if (f.kind === 'info') return '';
  if (f.clip && !to.length) return `${esc(from || '')} → 剪贴板`;
  return `${esc(from || '')}${to.length ? ' → ' + esc(to.join('、')) : ''}`;
}

// 动态：按天分组；可以按类型看（文字 / 图片 / 文件 / 手机 / 小云）；图片可以排成小图或中图。
let feedFilter = 'all';
let feedView = 'list';
try { feedView = localStorage.getItem('lb.feedView') || 'list'; } catch { }
function feedGroup(f) {
  const to = f.to || [], phones = new Set(S.phones.map(p => p.name));
  if (f.from === '小云' || to.includes('小云')) return 'cloud';
  if (phones.has(f.from) || to.some(n => phones.has(n))) return 'phone';
  return 'pc';
}
const isPic = f => !!(f.image || f.kind === 'image');
function feedType(f) { return f.kind === 'text' ? 'text' : isPic(f) ? 'image' : 'file'; }
function feedMatch(f, k) { return k === 'all' || (f.kind !== 'info' && (k === 'phone' || k === 'cloud' ? feedGroup(f) === k : feedType(f) === k)); }
function feedTabs(items) {
  const defs = [['all', '全部'], ['text', '文字'], ['image', '图片'], ['file', '文件'], ['phone', '手机'], ['cloud', '小云']];
  const shown = defs.filter(([k]) => k === 'all' || items.some(f => feedMatch(f, k)));
  if (!shown.some(([k]) => k === feedFilter)) feedFilter = 'all';
  const tabs = shown.length > 2 ? `<span class="ftabs">${shown.map(([k, l]) => `<button class="${feedFilter === k ? 'on' : ''}" data-ffilter="${k}">${l}</button>`).join('')}</span>` : '';
  const views = items.some(isPic) ? `<span class="ftabs fviews">${[['list', 'list', '列表：一行一条'], ['small', 'grid', '小图'], ['medium', 'image', '中图']].map(([k, ic, l]) => `<button class="${feedView === k ? 'on' : ''}" data-fview="${k}" title="${l}">${icon(ic)}</button>`).join('')}</span>` : '';
  return tabs + views;
}
function rowStatus(f) {
  const pct = f.total > 0 ? Math.min(100, Math.round(f.done * 100 / f.total)) : 0;
  if (f.status === 'sending') return `<span class="status wait">${f.total ? pct + '%' : '发送中'}</span>`;
  if (f.status === 'receiving') return `<span class="status wait">${pct}%</span>`;
  if (f.status === 'failed') return `<span class="status bad">${icon('x')}没成功</span>`;
  if (f.status === 'waiting') return `<span class="status wait">等手机下载</span>`;
  if (f.status === 'done') return `<span class="status ok" title="${f.dir === 'out' ? (f.clip ? '已同步' : '已送达') : (f.clip ? '已同步' : '已收到')}">${icon('check')}</span>`;
  return '';
}
function rowTitle(f) {
  if (f.kind === 'text') return f.text ? `“${esc(f.text.replace(/\s+/g, ' ').slice(0, 80))}”` : '文字';
  return feedTitle(f);
}
const picReady = f => isPic(f) && f.files[0] && f.files[0].exists && f.status !== 'sending' && f.status !== 'receiving' && feedGroup(f) !== 'cloud';
function tileHtml(f) {
  const r = route(f);
  return `<figure class="tile" title="${esc(f.files[0].name)}${r ? '\n' + r.replace(/<[^>]+>/g, '') : ''}">
    <img loading="lazy" src="/api/local/thumb?id=${f.id}&i=0&${tok()}" data-zoom="${f.id}" alt="">
    <figcaption><span>${esc(f.files[0].name)}</span><em>${esc(isMe(f.from) ? '本机' : f.from || '')} · ${hhmm(f.time)}</em></figcaption>
    <div class="tacts"><button class="iconbtn" data-recopy="${f.id}" title="重新放进剪贴板">${icon('copy')}</button><button class="iconbtn" data-reveal="${f.id}" title="在文件夹中显示">${icon('folder')}</button><button class="iconbtn" data-del="${f.id}" title="从动态里删掉这条">${icon('x')}</button></div>
  </figure>`;
}

function renderFeed() {
  const all = S.feed;
  $('clearFeed').classList.toggle('hide', all.length === 0);
  set($('feedHint'), feedTabs(all));
  const items = all.filter(f => feedMatch(f, feedFilter));
  const out = []; let day = '', gal = null;
  const flush = () => { if (gal) { out.push(`<li class="fgal ${feedView}">${gal.join('')}</li>`); gal = null; } };
  for (const f of items) {
    const dl = dayLabel(f.time);
    if (dl !== day) { flush(); day = dl; out.push(`<li class="fday">${dl}</li>`); }
    if (feedView !== 'list' && picReady(f)) { (gal = gal || []).push(tileHtml(f)); continue; }
    flush();
    if (f.kind === 'info') {
      out.push(`<li class="finfo"><span>${esc(f.note || '')} · ${hhmm(f.time)}</span><button class="iconbtn" data-del="${f.id}" title="删掉这条">${icon('x')}</button></li>`);
      continue;
    }
    const cloud = feedGroup(f) === 'cloud';
    const kindIcon = f.kind === 'text' ? 'text' : isPic(f) ? 'image' : f.files.length + (f.more || 0) > 1 ? 'files' : 'file';
    const anyFile = f.files.some(x => x.exists);
    const busy = f.status === 'sending' || f.status === 'receiving';
    const pct = f.total > 0 ? Math.min(100, f.done * 100 / f.total) : 0;
    const total = f.total || f.files.reduce((n, x) => n + (x.size || 0), 0);
    const meta = [];
    if (f.kind === 'text' && f.chars > 0) meta.push(f.chars + ' 字');
    else if (total > 0) meta.push(size(total));
    const r = route(f); if (r) meta.push(r);
    const names = f.files.map(x => x.name).join('\n');
    const one = !cloud && f.files.length === 1 && f.files[0].exists && !busy;
    const titleAttr = esc(f.kind === 'text' ? (f.text || '') : names);
    const title = cloud ? `<button class="ft link" data-open-cloud title="在小云里查看">${rowTitle(f)}</button>`
      : one ? `<button class="ft link" data-open="${f.id}" data-i="0" title="打开 ${titleAttr}">${rowTitle(f)}</button>`
      : f.kind === 'text' && f.text ? `<button class="ft link" data-recopy="${f.id}" title="点一下重新放进剪贴板\n\n${titleAttr}">${rowTitle(f)}</button>`
      : `<b class="ft" title="${titleAttr}">${rowTitle(f)}</b>`;
    let extra = '';
    // 只看文字时把内容多露几行，方便回头找
    const long = feedFilter === 'text' && f.kind === 'text' && f.text && (f.text.length > 60 || f.text.includes('\n'));
    if (busy && f.total > 0) extra += `<div class="bar"><i style="width:${pct.toFixed(1)}%"></i></div>`;
    if (f.status === 'failed' && f.note) extra += `<div class="fnote bad">${esc(f.note)}</div>`;
    const thumb = picReady(f) ? `<img class="mini" loading="lazy" src="/api/local/thumb?id=${f.id}&i=0&${tok()}" data-zoom="${f.id}" alt="">` : '';
    const acts = [];
    if (f.kind === 'text' && f.text) acts.push(`<button class="btn ghost small" data-recopy="${f.id}" title="重新放进剪贴板">${icon('copy')}复制</button>`);
    if (!cloud && (f.kind === 'image' || f.kind === 'files' || f.kind === 'send') && anyFile && !busy) {
      if (f.kind !== 'send') acts.push(`<button class="btn ghost small" data-recopy="${f.id}" title="重新放进剪贴板">${icon('copy')}复制</button>`);
      acts.push(`<button class="btn ghost small" data-reveal="${f.id}" title="在文件夹中显示">${icon('folder')}位置</button>`);
    }
    if (f.status === 'failed' && f.dir === 'out') acts.push(`<button class="btn ghost small" data-retry="${f.id}">${icon('refresh')}重试</button>`);
    acts.push(`<button class="iconbtn" data-del="${f.id}" title="从动态里删掉这条">${icon('x')}</button>`);
    out.push(`<li class="fi">
      <div class="fic">${icon(kindIcon)}</div>
      <div class="fb">${long ? `<button class="ftxt" data-recopy="${f.id}" title="点一下重新放进剪贴板">${esc(f.text.slice(0, 600))}</button>` : ''}<div class="fl">${long ? '' : title}<span class="fm">${meta.join(' · ')}</span></div>${extra}</div>
      <div class="fa">${acts.join('')}</div>
      ${thumb}
      <span class="fst">${rowStatus(f)}</span>
      <span class="fwhen">${hhmm(f.time)}</span>
    </li>`);
  }
  flush();
  if (all.length) {
    const days = S.settings.feedDays, total = S.feedTotal || all.length;
    out.push(`<li class="ffoot">记录保留 <select id="feedDays">${[[7, '7 天'], [30, '30 天'], [90, '90 天'], [0, '一直留着']].map(([v, l]) => `<option value="${v}" ${days === v ? 'selected' : ''}>${l}</option>`).join('')}</select><span>共 ${total} 条${total > all.length ? `，这里显示最近 ${all.length} 条` : ''} · 只删记录，不删收到的文件</span></li>`);
  }
  set($('feed'), out.join(''));
}
document.addEventListener('change', e => { if (e.target.id === 'feedDays') act('settings', { feedDays: +e.target.value }, '已改'); });

function sw(key, on) { return `<label class="switch"><input type="checkbox" data-set="${key}" ${on ? 'checked' : ''}><span></span></label>`; }

function renderSettings() {
  const st = S.settings, active = document.activeElement;
  if (active && $('settings').contains(active) && active.tagName === 'INPUT' && active.type !== 'checkbox') return;
  set($('settings'), `
    <div class="dh"><h2>设置</h2><button class="iconbtn" data-close-settings>${icon('x')}</button></div>
    <div class="group"><div class="gt">这台电脑</div><div class="gbox">
      <div class="row"><div class="rl"><b>名字</b><small>其他设备上显示的名字</small></div><input class="input" style="width:180px;height:32px" id="nameInput" value="${esc(S.me.name)}" maxlength="40"></div>
    </div></div>
    <div class="group"><div class="gt">自动同步</div><div class="gbox">
      <div class="row"><div class="rl"><b>复制的文字</b><small>这边复制，那边直接粘贴</small></div>${sw('syncText', st.syncText)}</div>
      <div class="row"><div class="rl"><b>截图、图片和复制的文件</b><small>单次不超过 <input class="input num" id="limitInput" value="${st.autoLimitMB}"> MB，更大的拖到设备上发</small></div>${sw('syncFiles', st.syncFiles)}</div>
      <div class="row"><div class="rl"><b>暂停全部同步</b><small>暂停时不收也不发</small></div>${sw('paused', st.paused)}</div>
    </div></div>
    <div class="group"><div class="gt">收到的文件</div><div class="gbox">
      <div class="row"><div class="rl"><b>存放位置</b><small title="${esc(st.receiveFolder)}">${esc(st.receiveFolder)}</small></div><button class="btn line small" data-native="receive-folder">更改</button><button class="btn ghost small" data-native="open-receive">打开</button></div>
      <div class="row"><div class="rl"><b>收到文件时提醒</b><small>右下角弹一条通知，点一下就能打开</small></div>${sw('notify', st.notify)}</div>
    </div></div>
    <div class="group"><div class="gt">Windows</div><div class="gbox">
      <div class="row"><div class="rl"><b>开机自动启动</b><small>在后台运行，开机就能同步；意外退出了 5 分钟内自己重新打开</small></div>${sw('autostart', st.autostart)}</div>
      <div class="row"><div class="rl"><b>右键“发送到 → 轻桥”</b><small>在文件上右键就能发</small></div>${sw('sendTo', st.sendTo)}</div>
    </div></div>
    ${S.phones.length ? `<div class="group"><div class="gt">连过的手机</div><div class="gbox">
      ${S.phones.map(p => `<div class="row"><div class="rl"><b>${esc(p.name)}</b><small>${p.state === 'online' ? '网页开着' : '上次 ' + when(p.seen)}</small></div><button class="btn line small" data-open-history="${p.id.replace('phone:', '')}">发来的内容</button><button class="btn danger small" data-remove="${p.id}">移除</button></div>`).join('')}
    </div></div>` : ''}
    ${relayGroup()}
    <div class="group"><div class="gt">网络</div><div class="gbox">
      <div class="row"><div class="rl"><b>本机地址</b><small>${S.me.addrs.length ? S.me.addrs.map(a => `<span class="addr">${a.ip}</span> ${esc(a.name)}`).join('<br>') : '没有连网'}</small><small>手机也可以用 <span class="addr">http://${esc(S.me.mdns)}:${S.me.port}</span></small></div></div>
      <div class="row"><div class="rl"><b>找不到其他设备？</b><small>Windows 防火墙可能拦住了轻桥（尤其是网络被设成“公用”时）</small></div><button class="btn line small" data-firewall>${icon('shield')}放行</button></div>
      <div class="row"><div class="rl"><b>重新查找</b><small>${S.scanning ? '正在找…' : S.lastScan ? '上次 ' + when(S.lastScan) : ''}${S.hop ? ' · 也在找上一级路由器 ' + S.hop + ' 下的电脑' : ''}</small></div><button class="btn line small" data-scan>${icon('refresh')}找一次</button></div>
    </div></div>
    <div class="group"><div class="gbox">
      <div class="row"><div class="rl"><b>轻桥 ${esc(S.version)}</b><small>关窗口不会退出，右下角托盘里还在运行</small></div><button class="btn danger small" data-native="exit">退出轻桥</button></div>
    </div></div>`);
}

const INSTALL_CMD = 'curl -fsSL https://raw.githubusercontent.com/snowylove666-tech/lanbridge/main/relay/install.sh | sudo bash -s -- --domain 你的域名';
function hostOf(u) { try { return new URL(u).host; } catch { return u || ''; } }

function relayGroup() {
  const r = S.relay, t = r.temp, on = !!r.url || t.on;
  let status;
  if (!on) status = '关着。打开后，家里和公司的电脑、用流量的手机都能互传。';
  else if (t.on && (t.state !== 'ok' || !r.url)) status = tempStatus(t);
  else if (r.state === 'error') status = `<span class="bad">连不上中转：${esc(r.error || '网络问题')}</span>`;
  else if (r.state !== 'ok') status = '正在连接中转…';
  else if (r.mode === 'temp') status = `已开启<b>临时中转</b>（这台电脑当中转，${esc(hostOf(r.url))}）。这台电脑关机时别处连不上；重启后地址会变，别处的电脑要再输入一次连接码。单次最多 ${r.maxMB} MB。`;
  else if (r.mode === 'custom') status = `已开启，用的是自己的中转（${esc(hostOf(r.url))}）。单次最多 ${r.maxMB} MB。`;
  else status = `已开启<b>常驻中转</b>。不在同一个网络的设备会自动经中转连接，单次最多 ${r.maxMB} MB。`;
  return `<div class="group" id="relayGroup"><div class="gt">跨网络</div><div class="gbox">
    <div class="row"><div class="rl"><b>不在同一个网络也能互传</b><small class="wrap">${status}</small></div>
      <label class="switch"><input type="checkbox" data-relay-switch ${on ? 'checked' : ''}><span></span></label></div>
    <details class="row stack fold2" ${r.mode === 'custom' ? 'open' : ''}><summary>用自己的服务器当常驻中转</summary>
      <small class="muted">在你的 Linux 服务器上运行这一行（有域名会自动配好 HTTPS），装好后把它显示的地址填到下面：</small>
      <div class="cmd"><code>${esc(INSTALL_CMD)}</code><button class="btn ghost small" data-copy-code="${esc(INSTALL_CMD)}">${icon('copy')}复制</button></div>
      <input class="input" id="relayInput" value="${esc(r.mode === 'custom' ? r.url : '')}" placeholder="https://relay.example.com" spellcheck="false">
      <small class="muted">组里的电脑会自动用同一个地址。</small></details>
  </div></div>`;
}

// 开启跨网络：选常驻还是临时，说清楚会发生什么，用户点“同意并开启”才开。
const CONSENT = {
  builtin: [
    '不在同一个网络的设备，会经过<b>轻桥的常驻中转服务器</b>连接；同一个网络里照样直连，不经过它。',
    '电脑之间的内容在电脑上就<b>加密</b>了，中转看不到；手机经中转打开网页时，中转能看到手机发送的内容。',
    '中转只转发、不保存。组里的其他电脑会跟着一起开启；随时可以在设置里关掉。'],
  temp: [
    '<b>用这台电脑当中转</b>，经 Cloudflare 的免费临时隧道连到外网，免注册。第一次要从 GitHub 下载 Cloudflare 官方的隧道程序 cloudflared（约 60 MB），会先校验 Cloudflare 的数字签名。',
    '这台电脑关机或退出轻桥时，别处的设备连不上；<b>每次重启地址都会变</b>，别处的电脑要再输入一次连接码，手机要重新扫码。国内网络有时连不上 Cloudflare。',
    '电脑之间的内容加密，Cloudflare 看不到；手机经它打开网页时，它能看到手机发送的内容。单次最多 95 MB。'],
};
function relayConsent() {
  return new Promise(done => {
    const builtIn = !!S.relay.builtIn;
    let mode = builtIn ? 'builtin' : 'temp';
    const m = document.createElement('div');
    m.className = 'modal';
    const draw = () => {
      m.innerHTML = `<div class="box consent">
        <div class="mh">${icon('shield')}<h2>开启跨网络</h2></div>
        <div class="choices">
          <button class="choice ${mode === 'builtin' ? 'on' : ''}" data-mode="builtin" ${builtIn ? '' : 'disabled'}><b>常驻中转${builtIn ? ' <em>推荐</em>' : ''}</b><small>${builtIn ? '轻桥的服务器，一直在线，地址不变' : '即将上线'}</small></button>
          <button class="choice ${mode === 'temp' ? 'on' : ''}" data-mode="temp"><b>临时中转</b><small>用这台电脑 + Cloudflare 临时隧道，这台电脑要开着</small></button>
        </div>
        <ul>${CONSENT[mode].map(x => `<li>${x}</li>`).join('')}</ul>
        <div class="actions" style="justify-content:flex-end"><button class="btn ghost" data-c="0">取消</button><button class="btn primary" data-c="1">${icon('check')}同意并开启</button></div>
      </div>`;
    };
    draw();
    const close = v => { m.remove(); done(v); };
    m.addEventListener('click', e => {
      const ch = e.target.closest('[data-mode]'); if (ch && !ch.disabled) { mode = ch.dataset.mode; draw(); return; }
      const b = e.target.closest('[data-c]'); if (b) close(b.dataset.c === '1' ? mode : null); else if (e.target === m) close(null);
    });
    document.body.appendChild(m);
  });
}
async function enableRelay() {
  const mode = await relayConsent();
  if (!mode) return false;
  if (mode === 'temp') return !!(await act('settings', { relayTemp: true }, '正在打开临时中转'));
  return !!(await act('settings', { relayEnable: true }, '已开启跨网络，正在连接中转'));
}

// ───────────── 小云（常驻中转上的文件池，单独一页） ─────────────

let cloudOpen = false, cloudData = null, cloudRev = -1, cloudReady = null, cloudLoading = false;

function cloudCard() {
  if (!S.cloud || !S.cloud.ready) return '';
  const used = cloudData && cloudData.ready ? `已用 ${cloudData.usedMB} MB / ${fmtCap(cloudData.capMB)}` : `${fmtCap(S.cloud.poolMB)} 空间`;
  return `<div class="dev cloud online" data-open-cloud data-dropdev="cloud">
      <div class="dic">${icon('cloud')}</div>
      <div class="dname">小云</div>
      <div class="dsub">${esc(used)} · 在哪都能下载</div>
      <div class="dact"><button class="btn soft small" data-cloud-pick="file">${icon('upload')}放文件</button></div>
    </div>`;
}
const fmtCap = mb => mb >= 1024 ? (mb / 1024).toFixed(mb % 1024 ? 1 : 0) + ' GB' : mb + ' MB';

function openCloud() {
  cloudOpen = true; cloudData = null; cloudRev = -1;
  closeSettings(); closeAdd();
  const el = $('cloud');
  el.innerHTML = `<div class="hwrap">
    <div class="hhead"><button class="btn ghost" data-cloud-close>${icon('back')}返回</button><h2>小云</h2><div class="grow"></div>
      <button class="btn ghost small" data-cloud-pick="folder">${icon('folder')}放文件夹</button><button class="btn primary small" data-cloud-pick="file">${icon('upload')}放文件</button></div>
    <div id="cloudBody"><div class="hempty"><span class="spin"></span></div></div>
  </div>`;
  el.classList.remove('hide'); el.scrollTop = 0; document.body.classList.add('noscroll');
  loadCloud();
}
function closeCloud() { cloudOpen = false; $('cloud').classList.add('hide'); $('cloud').innerHTML = ''; document.body.classList.remove('noscroll'); }

async function loadCloud() {
  if (cloudLoading) return;
  cloudLoading = true;
  try {
    const r = await api('cloud');
    cloudData = r; cloudRev = S.cloud.rev; cloudReady = S.cloud.ready;
    if (cloudOpen) renderCloud();
    renderDevices();
  } catch (e) { toast(e.message, true); }
  finally { cloudLoading = false; }
}

function renderCloud() {
  const d = cloudData; if (!d) return;
  if (!d.ready) {
    const why = d.reason === 'off' ? '小云放在常驻中转服务器上。先开启跨网络（选常驻中转），这里就能用。'
      : d.reason === 'down' ? '中转服务器暂时连不上，连上后这里会自动刷新。'
      : '现在用的中转没有小云（临时中转、旧版中转不带文件池）。换成常驻中转后就能用。';
    set($('cloudBody'), `<div class="hempty">${why}</div>`);
    return;
  }
  const pct = d.capMB ? Math.min(100, d.usedMB * 100 / d.capMB) : 0;
  const rows = d.files.map(f => `<li class="fi">
      <div class="fic">${icon('file')}</div>
      <div class="fb"><div class="fh"><b>${esc(f.name)}</b><span class="route">${esc(f.from || '')}</span><span>· ${size(f.size)} · ${dayLabel(f.at)} ${hhmm(f.at)}</span></div>
        <div class="clink" title="${esc(f.link)}">${esc(f.link.split('#')[0])}</div></div>
      <div class="fa"><button class="btn ghost small" data-cloud-down="${f.id}">${icon('down')}下载</button>
        <button class="btn ghost small" data-copy-code="${esc(f.link)}" title="手机或别人在浏览器里打开就能下载">${icon('link')}复制链接</button>
        <button class="iconbtn" style="width:28px;height:28px" data-cloud-del="${f.id}" title="从小云删掉">${icon('x')}</button></div>
    </li>`).join('');
  set($('cloudBody'), `
    <div class="cusage"><div class="bar"><i style="width:${pct.toFixed(1)}%"></i></div>
      <small>已用 ${d.usedMB} MB / ${fmtCap(d.capMB)}，单个最大 ${d.maxFileMB} MB。满了会先删最早放上来的。</small></div>
    ${d.error ? `<div class="bad" style="margin:0 2px 12px">${esc(d.error)}</div>` : ''}
    <div class="cdrop">${icon('upload')}<span>把文件拖到这个窗口，或者点右上角“放文件”。组里的电脑都能看到；“复制链接”发给手机或别人，浏览器打开就能下载。</span></div>
    ${d.files.length ? `<ul class="feed">${rows}</ul>` : '<div class="hempty">小云里还没有文件。</div>'}
    <div class="hnote">文件在这台电脑上加密后才上传，服务器上只有密文；链接里 # 后面是解密钥匙，只发给你信任的人。</div>`);
}

// ───────────── 手机发来的内容（单独一页，不占主界面） ─────────────

let histOpen = false, histPhone = '', histQ = '', histData = null, histRev = -1, histLoading = false;
const histOpenText = new Set();

function openHistory(phone) {
  histOpen = true; histPhone = phone || ''; histQ = ''; histData = null; histRev = -1; histOpenText.clear();
  closeSettings(); closeAdd();
  const el = $('history');
  el.innerHTML = `<div class="hwrap">
    <div class="hhead"><button class="btn ghost" data-hist-close>${icon('back')}返回</button><h2>手机发来的内容</h2><div class="grow"></div><button class="btn ghost small" data-hist-clear>${icon('trash')}清空</button></div>
    <div class="hbar"><input class="input" id="histSearch" placeholder="搜索文字或文件名" autocomplete="off" spellcheck="false"><div id="histPhones" class="hphones"></div></div>
    <div id="histList"><div class="hempty"><span class="spin"></span></div></div>
  </div>`;
  el.classList.remove('hide'); el.scrollTop = 0; document.body.classList.add('noscroll');
  loadHistory();
}
function closeHistory() { histOpen = false; $('history').classList.add('hide'); $('history').innerHTML = ''; document.body.classList.remove('noscroll'); }

async function loadHistory(more) {
  if (histLoading && !more) return;
  histLoading = true;
  try {
    const p = new URLSearchParams({ q: histQ, phone: histPhone, limit: '60' });
    if (more && histData && histData.items.length) p.set('before', histData.items[histData.items.length - 1].time);
    const r = await api('phone-history?' + p);
    if (!histOpen) return;
    histData = more && histData ? Object.assign(r, { items: histData.items.concat(r.items) }) : r;
    histRev = r.rev; renderHistory();
  } catch (e) { toast(e.message, true); }
  finally { histLoading = false; }
}

function dayLabel(ms) {
  const d = new Date(ms), t = new Date(); t.setHours(0, 0, 0, 0);
  const diff = Math.round((t - new Date(d.getFullYear(), d.getMonth(), d.getDate())) / 864e5);
  if (diff === 0) return '今天';
  if (diff === 1) return '昨天';
  return `${d.getMonth() + 1}月${d.getDate()}日 周${'日一二三四五六'[d.getDay()]}`;
}
const hhmm = ms => { const d = new Date(ms); return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0'); };

function histItemHtml(x) {
  const anyFile = x.files.some(f => f.exists);
  const kindIcon = x.kind === 'text' ? 'text' : x.kind === 'image' ? 'image' : x.files.length > 1 ? 'files' : 'file';
  const title = x.kind === 'text' ? `文字 <span class="muted">· ${x.chars} 字</span>` : x.kind === 'image' ? '照片' : x.files.length === 1 ? esc(x.files[0].name) : `${x.files.length} 个文件`;
  let body = '';
  if (x.kind === 'text') body = `<div class="htext ${histOpenText.has(x.id) ? 'open' : ''}" data-hexpand="${x.id}" title="点一下展开 / 收起">${esc(x.text)}${x.chars > x.text.length ? '\n…' : ''}</div>`;
  else if (x.kind === 'image' && x.files[0] && x.files[0].exists) body = `<img class="thumb" loading="lazy" src="/api/local/hthumb?id=${x.id}&${tok()}" data-zoom="1" alt="">`;
  if (x.kind !== 'text' && !(x.kind === 'image' && anyFile)) body += `<div class="ffiles">${x.files.map((f, i) => `<button class="chip ${f.exists ? '' : 'gone'}" ${f.exists ? `data-hopen="${x.id}" data-i="${i}"` : 'disabled'} title="${f.exists ? '打开' : '文件已不在'}">${icon('file')}<span>${esc(f.name)}</span><em>${size(f.size)}</em></button>`).join('')}</div>`;
  const acts = [];
  if (x.kind === 'text' || anyFile) acts.push(`<button class="btn ghost small" data-hcopy="${x.id}" title="放进剪贴板">${icon('copy')}复制</button>`);
  if (x.kind !== 'text' && anyFile) acts.push(`<button class="btn ghost small" data-hreveal="${x.id}" title="在文件夹中显示">${icon('folder')}位置</button>`);
  acts.push(`<button class="iconbtn" style="width:28px;height:28px" data-hdel="${x.id}" title="删掉这条">${icon('x')}</button>`);
  return `<li class="fi"><div class="fic">${icon(kindIcon)}</div>
    <div class="fb"><div class="fh"><b>${title}</b><span class="route">${esc(x.phone)}</span><span>· ${hhmm(x.time)}</span></div>${body}</div>
    <div class="fa">${acts.join('')}</div></li>`;
}

function renderHistory() {
  const d = histData; if (!d) return;
  const phones = d.phones || [];
  set($('histPhones'), phones.length > 1 ? [`<button class="hchip ${histPhone ? '' : 'on'}" data-hist-phone="">全部</button>`].concat(phones.map(p => `<button class="hchip ${histPhone === p.id ? 'on' : ''}" data-hist-phone="${p.id}">${esc(p.name)} <em>${p.count}</em></button>`)).join('') : '');
  if (!d.items.length) {
    set($('histList'), `<div class="hempty">${histQ ? '没有找到“' + esc(histQ) + '”' : '还没有手机发来的内容。<br>手机上在轻桥网页里粘贴、传照片，或者轻点背面发过来的，都会记在这里。'}</div>`);
    return;
  }
  const groups = [];
  for (const x of d.items) { const k = dayLabel(x.time); if (!groups.length || groups[groups.length - 1].k !== k) groups.push({ k, items: [] }); groups[groups.length - 1].items.push(x); }
  set($('histList'), groups.map(g => `<div class="hday">${g.k}</div><ol class="feed">${g.items.map(histItemHtml).join('')}</ol>`).join('')
    + (d.more ? `<div style="text-align:center;margin-top:14px"><button class="btn line small" data-hist-more>再往前看</button></div>` : '')
    + `<p class="hnote">共 ${d.total} 条，只存在这台电脑上（用你的 Windows 账户加密）。照片和文件在接收文件夹里。</p>`);
}

let histTimer = null;
document.addEventListener('input', e => {
  if (e.target.id !== 'histSearch') return;
  clearTimeout(histTimer);
  histTimer = setTimeout(() => { histQ = e.target.value.trim(); loadHistory(); }, 250);
});

// ───────────── 交互 ─────────────

document.addEventListener('click', async e => {
  const t = e.target.closest('button, [data-add], [data-zoom], [data-hexpand], .dev');
  if (!t) { if (openMenu) { openMenu = null; render(); } return; }
  const d = t.dataset;
  if (d.openHistory !== undefined) { openMenu = null; openHistory(d.openHistory); return; }
  if (d.openCloud !== undefined) { openCloud(); return; }
  if (d.ffilter) { feedFilter = d.ffilter; renderFeed(); return; }
  if (d.fview) { feedView = d.fview; try { localStorage.setItem('lb.feedView', feedView); } catch { } renderFeed(); return; }
  if (d.cloudClose !== undefined) { closeCloud(); return; }
  if (d.cloudDown !== undefined) { await act('cloud-download', { id: d.cloudDown }, '开始下载，完成后在“动态”里'); return; }
  if (d.cloudDel !== undefined) { if (confirm('从小云删掉这个文件？\n\n组里的电脑和链接都再也下载不了。')) { await act('cloud-delete', { id: d.cloudDel }, '已删除'); loadCloud(); } return; }
  if (d.cloudPick !== undefined) { await act('native', { cmd: 'pick', target: 'cloud', folder: d.cloudPick === 'folder' }); return; }
  if (d.histClose !== undefined) { closeHistory(); return; }
  if (d.histPhone !== undefined) { histPhone = d.histPhone; loadHistory(); return; }
  if (d.histMore !== undefined) { loadHistory(true); return; }
  if (d.histClear !== undefined) {
    const name = histPhone && histData ? (histData.phones.find(p => p.id === histPhone) || {}).name : '';
    if (!confirm(`清空${name ? '“' + name + '”' : '全部手机'}发来的内容记录？\n\n只删记录，不会删接收文件夹里的照片和文件。`)) return;
    if (await act('history-clear', { phone: histPhone }, '已清空')) loadHistory(); return;
  }
  if (d.hexpand) { if (window.getSelection().toString()) return; histOpenText.has(d.hexpand) ? histOpenText.delete(d.hexpand) : histOpenText.add(d.hexpand); t.classList.toggle('open'); return; }
  if (d.hcopy) { await act('history-copy', { id: d.hcopy }, '已放进剪贴板，可以直接粘贴'); return; }
  if (d.hdel) { if (await act('history-remove', { id: d.hdel })) loadHistory(); return; }
  if (d.hreveal) { await act('native', { cmd: 'hreveal', id: d.hreveal, i: 0 }); return; }
  if (d.hopen) { await act('native', { cmd: 'hopen', id: d.hopen, i: +d.i }); return; }
  if (t.classList.contains('dev') && (t.dataset.dropdev || '').startsWith('phone:') && !openMenu) { openHistory(t.dataset.dropdev.slice(6)); return; }
  if (d.menu !== undefined) { openMenu = openMenu === d.menu ? null : d.menu; render(); return; }
  if (openMenu && !t.closest('.menu')) { openMenu = null; render(); }
  if (d.answer) { await act('answer', { id: d.answer, allow: d.allow === '1' }, d.allow === '1' ? '已允许，正在连接' : null); return; }
  if (d.connect) { await act('connect', { id: d.connect }); return; }
  if (d.cancel) { await act('cancel-connect', { id: d.cancel }); return; }
  if (d.pick || d.pickFolder) { openMenu = null; const r = await act('native', { cmd: 'pick', target: d.pick || d.pickFolder, folder: !!d.pickFolder }); if (r && r.count) toast('开始发送 ' + r.count + ' 项'); return; }
  if (d.remove) {
    const dev = [...S.devices, ...S.phones].find(x => x.id === d.remove);
    openMenu = null;
    if (!confirm(`移除“${dev ? dev.name : ''}”？\n\n移除后它不再自动同步，要用的话需要重新连接。`)) { render(); return; }
    await act(d.remove.startsWith('phone:') ? 'remove-phone' : 'remove', { id: d.remove.replace(/^phone:/, '') }, '已移除'); return;
  }
  if (d.add !== undefined) { addOpen = true; scanAskedAt = Date.now(); $('addModal').classList.remove('hide'); renderAdd(); api('scan', {}).catch(() => {}); return; }
  if (d.closeAdd !== undefined) { closeAdd(); return; }
  if (d.qrip) { qrIp = d.qrip; render(); return; }
  if (d.qrlan !== undefined) { qrLan = d.qrlan === '1'; render(); return; }
  if (d.openSettings !== undefined) { openSettingsAt(d.openSettings); return; }
  if (d.relayEnable !== undefined) { if (await enableRelay()) remoteCodeAsked = false; return; }
  if (d.tempRetry !== undefined) { await act('settings', { relayTemp: true }, '正在重新打开临时中转'); return; }
  if (d.relayOff !== undefined) { await act('settings', { relay: '' }, '已关闭跨网络'); return; }
  if (d.copyCode !== undefined) { try { await navigator.clipboard.writeText(d.copyCode); toast('已复制'); } catch { toast('复制不了，请手动选中复制', true); } return; }
  if (d.remoteConnect !== undefined) { const v = $('remoteCode').value.trim(); if (!v) { toast('先输入连接码', true); return; } if (await act('connect-code', { code: v }, '正在经中转请求连接')) $('remoteCode').value = ''; return; }
  if (d.scan !== undefined) { scanAskedAt = Date.now(); await act('scan', {}, '正在重新查找'); return; }
  if (d.firewall !== undefined) { const r = await act('native', { cmd: 'firewall' }); if (r) toast(r.ok ? '已放行。可以让另一台再找一次' : '没有完成（需要在弹出的窗口点“是”）', !r.ok); return; }
  if (d.manual !== undefined) { const v = $('manualAddr').value.trim(); if (!v) { toast('先填地址', true); return; } await act('connect-address', { address: v }, '已找到，正在请求连接'); return; }
  if (d.recopy) { await act('recopy', { id: d.recopy }, '已放进剪贴板'); return; }
  if (d.open) { await act('native', { cmd: 'open', id: d.open, i: +d.i }); return; }
  if (d.reveal) { await act('native', { cmd: 'reveal', id: d.reveal, i: 0 }); return; }
  if (d.retry) { await act('retry', { id: d.retry }, '重新发送'); return; }
  if (d.del) { await act('feed-remove', { id: d.del }); return; }
  if (d.native) { if (d.native === 'exit' && !confirm('退出轻桥？退出后不再同步，也收不到文件。')) return; await act('native', { cmd: d.native }); return; }
  if (d.sendPending) { await act('send', { target: d.sendPending, pending: true }, '开始发送'); return; }
  if (d.cancelPending !== undefined) { await act('cancel-pending', {}); return; }
  if (d.closeSettings !== undefined) { closeSettings(); return; }
  if (d.zoom) { const img = document.createElement('div'); img.className = 'lightbox'; img.innerHTML = `<img src="${t.src}">`; img.onclick = () => img.remove(); document.body.appendChild(img); return; }
});

document.addEventListener('change', async e => {
  if (e.target.matches('[data-relay-switch]')) {
    if (e.target.checked) { e.target.checked = false; await enableRelay(); }
    else if (confirm('关掉跨网络？\n\n不在同一个网络的设备会断开，组里的其他电脑也会跟着关掉。')) await act('settings', { relay: '' }, '已关闭跨网络');
    else e.target.checked = true;
    return;
  }
  const k = e.target.dataset.set;
  if (k) {
    const body = {}; body[k] = e.target.checked;
    await act('settings', body, k === 'paused' ? (e.target.checked ? '已暂停' : '已恢复') : null);
    return;
  }
  if (e.target.id === 'nameInput') { const v = e.target.value.trim(); if (v) await act('settings', { name: v }, '名字已改'); }
  if (e.target.id === 'relayInput') {
    const v = e.target.value.trim();
    if (v) await act('settings', { relay: v }, '已保存，正在连接你的中转');
    else if (S.relay.mode === 'custom') await act('settings', { relay: '' }, '已关闭跨网络');
    return;
  }
  if (e.target.id === 'limitInput') { const v = parseInt(e.target.value, 10); if (v > 0) await act('settings', { autoLimitMB: v }, '已保存'); }
});

$('sync').onclick = () => act('settings', { paused: !S.settings.paused }, S.settings.paused ? '已恢复同步' : '已暂停同步');
$('openSettings').onclick = () => { settingsOpen = true; renderSettings(); $('settings').classList.add('open'); $('scrim').classList.remove('hide'); };
$('scrim').onclick = () => closeSettings();
function openSettingsAt(section) {
  closeAdd();
  settingsOpen = true; renderSettings(); $('settings').classList.add('open'); $('scrim').classList.remove('hide');
  if (!section) return;
  // 抽屉滑出来以后再滚（滑动中滚不动）；每次都重新取元素，界面可能刚刷新过。
  setTimeout(() => {
    const g = document.getElementById(section + 'Group'), box = $('settings'); if (!g) return;
    box.scrollTop = Math.max(0, g.offsetTop - 70);
    g.classList.add('flash');
    setTimeout(() => { const x = document.getElementById(section + 'Group'); if (x) x.classList.remove('flash'); }, 1800);
  }, 260);
}
function closeSettings() { settingsOpen = false; $('settings').classList.remove('open'); $('scrim').classList.add('hide'); }
function closeAdd() { addOpen = false; remoteOpen = false; $('addModal').classList.add('hide'); }
$('addModal').addEventListener('click', e => { if (e.target === $('addModal')) closeAdd(); });
$('clearFeed').onclick = () => { if (confirm('清空动态？（不会删除收到的文件）')) act('feed-clear', {}); };
document.addEventListener('keydown', e => { if (e.key === 'Escape') { if (cloudOpen) { closeCloud(); return; } if (histOpen && !document.querySelector('.lightbox')) { closeHistory(); return; } closeSettings(); closeAdd(); if (S && S.pending) act('cancel-pending', {}); } });
document.addEventListener('keydown', e => {
  if (e.key === 'Enter' && e.target.id === 'manualAddr') document.querySelector('[data-manual]').click();
  if (e.key === 'Enter' && e.target.id === 'remoteCode') document.querySelector('[data-remote-connect]').click();
  if (e.key === 'Enter' && e.target.id === 'relayInput') e.target.blur();
});
// 打开“不在同一个网络？”时才生成配对码（10 分钟有效）。
document.addEventListener('toggle', e => {
  if (!e.target.matches || !e.target.matches('details[data-remote]')) return;
  remoteOpen = e.target.open;
  if (remoteOpen && S && S.relay.url && S.relay.state === 'ok' && !S.relay.code) { remoteCodeAsked = true; act('remote-code', {}); }
  if (!remoteOpen) remoteCodeAsked = false;
}, true);

// 改名字：点一下顶栏的名字
$('me').onclick = () => {
  if (editingName) return;
  editingName = true;
  $('me')._h = null;
  $('me').innerHTML = `<span class="me-label">本机</span><input class="input" id="meInput" maxlength="40" value="${esc(S.me.name)}">`;
  const i = $('meInput'); i.focus(); i.select();
  const done = async save => { if (!editingName) return; editingName = false; const v = i.value.trim(); if (save && v && v !== S.me.name) await act('settings', { name: v }, '名字已改'); render(); };
  i.onkeydown = ev => { if (ev.key === 'Enter') done(true); if (ev.key === 'Escape') done(false); };
  i.onblur = () => done(true);
};

window.addEventListener('scroll', () => document.querySelector('.top').classList.toggle('scrolled', scrollY > 4));

// ───────────── 拖文件进来 ─────────────

let dragDepth = 0;
const hasFiles = e => e.dataTransfer && [...e.dataTransfer.types].includes('Files');
window.addEventListener('dragenter', e => {
  if (!hasFiles(e)) return;
  e.preventDefault(); dragDepth++;
  if (dragDepth === 1) showDrop();
});
window.addEventListener('dragleave', e => { if (!hasFiles(e)) return; dragDepth = Math.max(0, dragDepth - 1); if (dragDepth === 0) hideDrop(); });
window.addEventListener('dragover', e => {
  if (!hasFiles(e)) return;
  e.preventDefault(); e.dataTransfer.dropEffect = 'copy';
  document.querySelectorAll('.dz').forEach(z => z.classList.toggle('over', z.contains(e.target)));
});
window.addEventListener('drop', e => {
  if (!hasFiles(e)) return;
  e.preventDefault(); dragDepth = 0;
  const z = e.target.closest && e.target.closest('.dz');
  hideDrop();
  if (z && z.dataset.dz) sendFiles(z.dataset.dz, e.dataTransfer.files, z.dataset.name);
});
function showDrop() {
  if (!S) return;
  const all = [...S.devices, ...S.phones];
  const zones = all.map(d => `<div class="dz" data-dz="${d.id}" data-name="${esc(d.name)}">${icon(devIcon(d))}${esc(d.name)}<small>${d.state === 'offline' ? (d.kind === 'phone' ? '放进它的收件箱' : '上线后自动送达') : '在线'}</small></div>`);
  if (S.cloud.ready) zones.push(`<div class="dz" data-dz="cloud" data-name="小云">${icon('cloud')}小云<small>放上去，在哪都能下载</small></div>`);
  $('dzGrid').innerHTML = zones.length ? zones.join('') : '<p class="muted">还没有连接其他设备，先添加一台。</p>';
  $('dropzone').classList.remove('hide');
}
function hideDrop() { $('dropzone').classList.add('hide'); }
async function sendFiles(target, files, name) {
  if (!files || !files.length) return;
  if (hasWebView) { chrome.webview.postMessageWithAdditionalObjects({ cmd: 'drop', target }, files); toast(`开始发给 ${name}：${files.length} 项`); return; }
  // 在普通浏览器里打开时：先传到本机再发。
  toast(`正在准备 ${files.length} 个文件…`);
  for (const f of files) {
    const r = await fetch(`/api/local/upload?target=${encodeURIComponent(target)}&name=${encodeURIComponent(f.name)}`, { method: 'POST', headers: { 'X-LB-Local': TOKEN }, body: f });
    if (!r.ok) { const j = await r.json().catch(() => ({})); toast(j.error || '发送失败', true); return; }
  }
  toast(`开始发给 ${name}`);
}
if (window.chrome && chrome.webview) chrome.webview.addEventListener('message', e => { if (e.data && e.data.error) toast(e.data.error, true); });

setInterval(() => { if (S) { renderFeed(); if (!S.devices.length && !S.phones.length) return; renderDevices(); } }, 30000);
loop();
