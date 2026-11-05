// 轻桥跨网络测试：本机起中转服务和两台“不在同一个网络”的电脑（--no-lan：互相不走局域网，只能经中转），
// 走一遍：配对码配对 → 文字/截图/文件同步 → 发大文件 → 手机经中转 → 安全 → 换中转地址 → 中转重启 → 离线排队；
// 最后再起两台能直连的，看配对走中转、之后自动改成直连。
// 用法：node tests/relay.mjs [path\to\LanBridge.exe]
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import http from 'node:http';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const exe = path.resolve(process.argv[2] || 'dist/LanBridge.exe');
const relayJs = path.join(here, '..', 'relay', 'relay.mjs');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lb2-relay-'));
const procs = [];
let passed = 0, failed = 0;
const sleep = ms => new Promise(r => setTimeout(r, ms));
function ok(cond, name, extra) { if (cond) { passed++; console.log('  ✓ ' + name); } else { failed++; console.log('  ✗ ' + name + (extra ? '  ← ' + extra : '')); } }
const sha = b => crypto.createHash('sha256').update(b).digest('hex');

// RELAY_IMPL=pc：中转换成电脑里内置的那份（临时中转用的 RelayServer），同一套检查再跑一遍。
const IMPL = process.env.RELAY_IMPL === 'pc' ? 'pc' : 'node';
let relaySeq = 0;
function startRelay(port) {
  const p = IMPL === 'pc'
    ? spawn(exe, ['--data', path.join(root, 'relayhost-' + port + '-' + (relaySeq++)), '--port', String(port + 100), '--headless', '--loopback', '--fake-clipboard', '--no-lan', '--relay-host', String(port), '--relay-max-mb', '64'], { stdio: 'ignore' })
    : spawn(process.execPath, [relayJs], { env: { ...process.env, PORT: String(port), STATE: path.join(root, 'relay-' + port + '.json'), MAX_BODY_MB: '64' }, stdio: 'ignore' });
  procs.push(p); return { port, url: 'http://127.0.0.1:' + port, proc: p };
}
async function relayUp(r) { for (let i = 0; i < 100; i++) { try { if ((await fetch(r.url + '/health')).ok) return; } catch { } await sleep(100); } throw new Error('中转没起来'); }
function start(name, port, relay, lan, extra = []) {
  const data = path.join(root, name);
  const args = ['--data', data, '--port', String(port), '--headless', '--loopback', '--fake-clipboard', '--name', name, ...extra];
  if (relay) args.push('--relay', relay);
  if (!lan) args.push('--no-lan');
  const p = spawn(exe, args, { stdio: 'ignore' }); procs.push(p);
  return { name, port, data, base: 'http://127.0.0.1:' + port, token: null, proc: p };
}
async function ready(n) {
  for (let i = 0; i < 100; i++) {
    try { const u = fs.readFileSync(path.join(n.data, 'desk-url.txt'), 'utf8'); n.token = u.split('#t=')[1].trim(); await state(n); return; } catch { await sleep(150); }
  }
  throw new Error(n.name + ' 没起来');
}
async function call(n, p, body) {
  const r = await fetch(n.base + p, { method: body === undefined ? 'GET' : 'POST', headers: { 'X-LB-Local': n.token, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const t = await r.text(); let j; try { j = JSON.parse(t); } catch { j = t; }
  if (!r.ok) throw new Error(p + ' ' + r.status + ' ' + (j.error || t));
  return j;
}
const state = n => call(n, '/api/local/state');
const local = (n, p, b) => call(n, '/api/local/' + p, b);
const test = (n, p, b) => call(n, '/api/test/' + p, b);
async function until(fn, ms = 20000, step = 250) {
  const end = Date.now() + ms; let last;
  while (Date.now() < end) { try { last = await fn(); if (last) return last; } catch (e) { last = e; } await sleep(step); }
  return null;
}
function raw(url, headers) { // 用原始 http 请求，才能带上 Origin 这类头
  return new Promise(res => { const u = new URL(url); http.get({ host: u.hostname, port: u.port, path: u.pathname + u.search, headers }, r => { r.resume(); res(r.statusCode); }).on('error', () => res(0)); });
}
function png(w, h) {
  const rawb = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const o = y * (w * 4 + 1) + 1 + x * 4; rawb[o] = x & 255; rawb[o + 1] = y & 255; rawb[o + 2] = 200; rawb[o + 3] = 255; }
  const crcT = []; for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; crcT[n] = c >>> 0; }
  const crc = b => { let c = 0xffffffff; for (const x of b) c = crcT[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (t, d) => { const l = Buffer.alloc(4); l.writeUInt32BE(d.length); const td = Buffer.concat([Buffer.from(t), d]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([l, td, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(rawb)), chunk('IEND', Buffer.alloc(0))]);
}

async function main() {
  console.log('程序：' + exe + '\n数据：' + root + '\n中转：' + (IMPL === 'pc' ? '电脑内置（RelayServer）' : 'relay/relay.mjs'));
  const R = startRelay(49460); await relayUp(R);
  const A = start('家里电脑', 49421, R.url), B = start('公司电脑', 49422, R.url);
  await ready(A); await ready(B);
  const aId = (await test(A, 'info')).id, bId = (await test(B, 'info')).id;

  console.log('\n1. 连上中转');
  ok(await until(async () => (await state(A)).relay.state === 'ok' && (await state(B)).relay.state === 'ok', 10000), '两台电脑都连上了中转服务');
  ok((await state(A)).nearby.length === 0, '两台互相发现不了（模拟不在同一个网络）');

  console.log('\n2. 配对码配对');
  const code = (await local(A, 'remote-code', {})).code;
  ok(/^[2-9A-HJ-NP-Z]{8}$/.test(code), 'A 生成 8 位配对码 ' + code.slice(0, 4) + '-' + code.slice(4));
  ok(await until(async () => (await fetch(R.url + '/c/' + code + '/lb/hello')).ok, 8000), '中转马上认得这个配对码');
  const wrong = await fetch(R.url + '/c/ABCDEFGH/lb/join', { method: 'POST', body: '{}' });
  ok(wrong.status === 404, '错的配对码找不到任何电脑');
  await local(B, 'connect-code', { code: code.slice(0, 4) + '-' + code.slice(4).toLowerCase() });
  const req = await until(async () => (await state(A)).requests[0]);
  ok(req && req.kind === 'pc' && /^\d{6}$/.test(req.code), 'A 收到“公司电脑想连接”，带 6 位核对码', JSON.stringify(req));
  const bNear = await until(async () => (await state(B)).nearby.find(n => n.code));
  ok(bNear && bNear.code === req.code && bNear.name === '家里电脑', '两边核对码一致，B 上显示对方名字', bNear && JSON.stringify(bNear));
  await local(A, 'answer', { id: req.id, allow: true });
  const aSees = await until(async () => (await state(A)).devices.find(d => d.id === bId && d.state === 'online'));
  const bSees = await until(async () => (await state(B)).devices.find(d => d.id === aId && d.state === 'online'));
  ok(aSees && bSees, '两台互相显示在线');
  ok(aSees && aSees.route === 'relay' && bSees && bSees.route === 'relay', '连接方式是“经中转”', aSees && aSees.route);

  console.log('\n3. 剪贴板同步（经中转）');
  const t1 = '跨网络 ' + Date.now() + '\n第二行 🌏';
  let t0 = Date.now();
  await test(A, 'copy', { kind: 'text', text: t1 });
  ok(await until(async () => (await test(B, 'clip')).text === t1, 8000, 50), `A 复制文字 → B 剪贴板（${Date.now() - t0} ms）`);
  await sleep(400);
  const t2 = '公司复制的 ' + Date.now();
  t0 = Date.now();
  await test(B, 'copy', { kind: 'text', text: t2 });
  ok(await until(async () => (await test(A, 'clip')).text === t2, 8000, 50), `B 复制文字 → A 剪贴板（${Date.now() - t0} ms）`);
  const img = png(400, 300), imgPath = path.join(root, 'shot.png'); fs.writeFileSync(imgPath, img);
  await test(A, 'copy', { kind: 'image', paths: [imgPath] });
  const bc = await until(async () => { const c = await test(B, 'clip'); return c.kind === 'image' && c.paths && c.paths[0] ? c : null; }, 10000);
  ok(bc && sha(fs.readFileSync(bc.paths[0])) === sha(img), 'A 截图 → B 剪贴板里是同一张图');
  const f1 = path.join(root, '合同.pdf'), f2 = path.join(root, 'data.bin');
  fs.writeFileSync(f1, crypto.randomBytes(500 * 1024)); fs.writeFileSync(f2, crypto.randomBytes(3 * 1024 * 1024 + 5));
  await test(B, 'copy', { kind: 'files', paths: [f1, f2] });
  const ac = await until(async () => { const c = await test(A, 'clip'); return c.kind === 'files' && c.paths && c.paths.length === 2 ? c : null; }, 10000);
  ok(ac && sha(fs.readFileSync(ac.paths[0])) === sha(fs.readFileSync(f1)) && sha(fs.readFileSync(ac.paths[1])) === sha(fs.readFileSync(f2)), 'B 复制两个文件 → A 剪贴板里是这两个文件');
  const huge = path.join(root, '视频.mp4'); fs.writeFileSync(huge, crypto.randomBytes(21 * 1024 * 1024));
  await test(A, 'copy', { kind: 'files', paths: [huge] });
  const skipNote = await until(async () => (await state(A)).feed.find(f => f.dir === 'out' && f.note && f.note.includes('不在同一个网络')), 5000);
  ok(skipNote, '复制超过 20 MB 的文件不经中转自动同步（提示可以拖过去发）', skipNote && skipNote.note);

  console.log('\n4. 发文件（经中转）');
  const big = path.join(root, '大文件.zip'); fs.writeFileSync(big, crypto.randomBytes(24 * 1024 * 1024 + 3));
  const dir = path.join(root, '资料'); fs.mkdirSync(dir); fs.writeFileSync(path.join(dir, 'a.txt'), 'hello'); fs.mkdirSync(path.join(dir, 'sub')); fs.writeFileSync(path.join(dir, 'sub', 'b.txt'), 'world');
  t0 = Date.now();
  await local(A, 'send', { target: bId, paths: [big, dir] });
  const bRecv = path.join(B.data, '收到的文件');
  const got = await until(async () => fs.existsSync(path.join(bRecv, '大文件.zip')) && fs.existsSync(path.join(bRecv, '资料.zip')), 60000);
  ok(got && sha(fs.readFileSync(path.join(bRecv, '大文件.zip'))) === sha(fs.readFileSync(big)), `24 MB 文件 + 文件夹经中转送达，内容一致（${((Date.now() - t0) / 1000).toFixed(1)} 秒）`);
  ok(got && !fs.readdirSync(bRecv).some(n => n.endsWith('.lbpart')), '没有留下半成品');
  const tooBig = path.join(root, '超大.iso'); fs.writeFileSync(tooBig, Buffer.alloc(65 * 1024 * 1024));
  await local(A, 'send', { target: bId, paths: [tooBig] });
  const tb = await until(async () => (await state(A)).feed.find(f => f.kind === 'send' && f.status === 'failed' && f.note && f.note.includes('MB')), 10000);
  ok(tb, '超过中转上限的文件直接提示“大文件请在同一个网络里传”，不反复重试', tb && tb.note);

  console.log('\n5. 手机经中转（用流量也能用）');
  const M = R.url + '/m/' + aId;
  const redirect = await fetch(M, { redirect: 'manual' });
  ok(redirect.status === 301 && redirect.headers.get('location') === '/m/' + aId + '/', '短地址自动补上斜杠');
  const page = await (await fetch(M + '/')).text();
  ok(page.includes('assets/phone.js') && !page.includes('"/assets/'), '手机网页经中转能打开（资源用相对地址）');
  ok((await fetch(M + '/assets/phone.js')).ok && (await fetch(M + '/assets/manifest.json')).ok && (await fetch(M + '/apple-touch-icon.png')).ok, '网页的脚本、图标都能经中转加载');
  const sA = await state(A);
  ok(sA.pair.relayUrl === R.url + '/m/' + aId + '/', '电脑上的二维码用中转地址', sA.pair.relayUrl);
  const pair = (await test(A, 'info')).pair;
  const pj = await (await fetch(M + '/api/phone/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ secret: pair, name: 'iPhone 流量' }) })).json();
  ok(pj.token && pj.pc === '家里电脑', '扫码经中转直接连上', JSON.stringify(pj).slice(0, 80));
  const ph = { 'X-LB-Token': pj.token, 'Content-Type': 'application/json' };
  const st = await (await fetch(M + '/api/phone/state', { headers: ph })).json();
  ok(st.pc === '家里电脑' && st.relay === R.url + '/m/' + aId + '/', '手机看到电脑剪贴板');
  const t3 = '手机用流量发的 ' + Date.now();
  await fetch(M + '/api/phone/text', { method: 'POST', headers: ph, body: JSON.stringify({ text: t3 }) });
  ok((await test(A, 'clip')).text === t3, '手机发文字 → 家里电脑剪贴板');
  ok(await until(async () => (await test(B, 'clip')).text === t3, 8000), '→ 也同步到公司电脑');
  const up = await fetch(M + '/api/phone/upload?name=' + encodeURIComponent('IMG_0002.png') + '&clip=1', { method: 'POST', headers: { 'X-LB-Token': pj.token }, body: img });
  ok(up.ok && (await test(A, 'clip')).kind === 'image', '手机传照片 → 电脑剪贴板里是图片');
  const before = (await (await fetch(M + '/api/phone/state', { headers: ph })).json()).rev;
  const waitP = fetch(M + '/api/phone/state?rev=' + before, { headers: ph }).then(r => r.json());
  await sleep(500); const t4 = '推给手机 ' + Date.now(); await test(A, 'copy', { kind: 'text', text: t4 });
  const pushed = await waitP;
  ok(pushed.clip && pushed.clip.text === t4, '电脑复制后，经中转开着的手机网页立刻更新');
  const swap = async body => (await fetch(M + '/v1/swap', { method: 'POST', headers: { Authorization: 'Bearer ' + pj.token, 'Content-Type': 'text/plain' }, body })).text();
  await test(A, 'copy', { kind: 'text', text: '电脑刚复制的' });
  ok(await swap('手机旧的') === '电脑刚复制的', '轻点背面经中转：电脑的文字带回手机');
  await local(A, 'send', { target: (await state(A)).phones[0].id, paths: [f1] });
  const st2 = await (await fetch(M + '/api/phone/state', { headers: ph })).json();
  const dl = Buffer.from(await (await fetch(M + '/api/phone/file?inbox=' + st2.inbox[0].id + '&t=' + pj.token)).arrayBuffer());
  ok(sha(dl) === sha(fs.readFileSync(f1)), '电脑发给手机的文件经中转下载，内容一致');

  console.log('\n6. 安全');
  ok((await fetch(R.url + '/t/' + aId + '/api/local/state')).status === 403, '经中转访问不到本机界面接口');
  ok((await fetch(M + '/api/local/state')).status === 403, '手机通道也访问不到本机界面接口');
  ok((await fetch(M + '/get')).status === 403, '经中转下载不到程序本体');
  ok((await fetch(R.url + '/t/' + aId + '/lb/join', { method: 'POST', body: '{}' })).status === 403, '不知道配对码的人不能经中转发起配对');
  const forged = await fetch(R.url + '/t/' + aId + '/lb/offer', { method: 'POST', headers: { 'X-LB-Id': bId, 'X-LB-Ts': String(Date.now()), 'X-LB-Nonce': 'n1', 'X-LB-Sig': 'AAAA' }, body: 'x'.repeat(3000) });
  ok(forged.status === 401, '没有组密钥的请求经中转也被拒绝');
  ok(await raw(M + '/api/phone/state', { 'X-LB-Token': pj.token, Origin: 'https://evil.example' }) === 403, '别的网站借手机浏览器发来的请求被拒绝');
  ok((await fetch(R.url + '/p/' + aId + '?k=' + 'f'.repeat(32))).status === 403, '别人冒充这台电脑去中转收请求会被拒绝');
  ok((await fetch(M + '/api/phone/state', { headers: { 'X-LB-Token': 'x'.repeat(48) } })).status === 401, '没连接的手机经中转拿不到内容');

  console.log('\n7. 换中转地址：组里自动跟着换');
  const R2 = startRelay(49461); await relayUp(R2);
  await local(A, 'settings', { relay: R2.url });
  ok(await until(async () => (await state(B)).relay.url === R2.url, 20000), 'A 改了地址，B 自动换成同一个');
  ok(await until(async () => (await state(A)).devices.find(d => d.id === bId && d.state === 'online') && (await state(B)).devices.find(d => d.id === aId && d.state === 'online'), 30000), '换过去以后两台仍然在线');
  const t5 = '换了中转 ' + Date.now(); await test(A, 'copy', { kind: 'text', text: t5 });
  ok(await until(async () => (await test(B, 'clip')).text === t5, 10000), '同步照常');

  console.log('\n8. 中转重启、对方离线');
  R2.proc.kill(); await sleep(1500);
  ok(await until(async () => (await state(A)).relay.state === 'error', 15000), '中转停了，界面显示“连不上”');
  const R3 = startRelay(49461); await relayUp(R3);
  ok(await until(async () => (await state(A)).relay.state === 'ok' && (await state(B)).relay.state === 'ok', 40000), '中转回来后自动重连');
  ok(await until(async () => (await state(A)).devices.find(d => d.id === bId && d.state === 'online'), 40000), '两台重新在线');
  B.proc.kill(); await sleep(500);
  const late = path.join(root, '晚点到.txt'); fs.writeFileSync(late, '对方上线后送达 ' + Date.now());
  ok(await until(async () => (await state(A)).devices.find(d => d.id === bId && d.state === 'offline'), 70000), 'B 关机后显示离线');
  await local(A, 'send', { target: bId, paths: [late] });
  const B2 = start('公司电脑', 49422, R.url); await ready(B2);
  const lateGot = await until(async () => fs.existsSync(path.join(bRecv, '晚点到.txt')), 60000);
  ok(lateGot && fs.readFileSync(path.join(bRecv, '晚点到.txt'), 'utf8') === fs.readFileSync(late, 'utf8'), 'B 开机后自动收到离线时发的文件');
  ok((await state(B2)).relay.url === R2.url, 'B 重启后记得新的中转地址');

  console.log('\n9. 能直连时不绕中转');
  const E = start('笔记本', 49425, R3.url, true), F = start('台式机', 49426, R3.url, true);
  await ready(E); await ready(F);
  const fId = (await test(F, 'info')).id;
  await until(async () => (await state(E)).relay.state === 'ok' && (await state(F)).relay.state === 'ok', 10000);
  const code2 = (await local(F, 'remote-code', {})).code;
  await sleep(600);
  await local(E, 'connect-code', { code: code2 });
  const r2 = await until(async () => (await state(F)).requests[0]);
  if (r2) await local(F, 'answer', { id: r2.id, allow: true });
  const eSees = await until(async () => (await state(E)).devices.find(d => d.id === fId && d.state === 'online'));
  ok(eSees && eSees.route === 'lan', '配对码配对后，发现能直连就直接连（不经中转）', eSees && JSON.stringify(eSees));

  console.log('\n10. 一键开启 / 关闭');
  const builtIn = (await state(E)).relay.builtIn;
  let enableErr = ''; try { await local(E, 'settings', { relayEnable: true }); } catch (e) { enableErr = e.message; }
  if (builtIn) ok(!enableErr && (await state(E)).relay.url === builtIn, '“开启跨网络”直接用内置中转', enableErr);
  else ok(/还没上线/.test(enableErr), '内置中转还没上线时，“开启跨网络”给出明确提示', enableErr);
  await local(E, 'settings', { relay: '' });
  ok(await until(async () => !(await state(E)).relay.url && !(await state(F)).relay.url && (await state(F)).relay.state === 'off', 20000), '一台关掉跨网络，组里的另一台也跟着关');

  console.log('\n11. 临时中转（这台电脑自己当中转；测试时不连 Cloudflare，隧道地址用本机地址代替）');
  const G = start('出差笔记本', 49427, null, false, ['--fake-tunnel']), H = start('家里台式', 49428, null, false);
  await ready(G); await ready(H);
  const gId = (await test(G, 'info')).id;
  await local(G, 'settings', { relayTemp: true });
  const gs = await until(async () => { const s = await state(G); return s.relay.temp.state === 'ok' && s.relay.state === 'ok' ? s : null; }, 20000);
  ok(gs && gs.relay.mode === 'temp' && /^http:\/\/127\.0\.0\.1:\d+$/.test(gs.relay.url), '开启临时中转：本机起中转、拿到“隧道地址”并连上', gs && JSON.stringify(gs.relay));
  const c3 = (await local(G, 'remote-code', {})).code;
  const ct = await until(async () => (await state(G)).relay.codeText);
  ok(ct && ct.startsWith(c3.slice(0, 4) + '-' + c3.slice(4) + '@127.0.0.1:'), '连接码后面带上中转地址：' + ct);
  let e3 = ''; try { await local(H, 'connect-code', { code: c3 }); } catch (e) { e3 = e.message; }
  ok(/还没开跨网络/.test(e3), '只输 8 位码、这台又没开跨网络：提示把整个连接码粘贴进来', e3);
  await local(H, 'connect-code', { code: ct });
  const r3 = await until(async () => (await state(G)).requests[0]);
  if (r3) await local(G, 'answer', { id: r3.id, allow: true });
  const hSees = await until(async () => (await state(H)).devices.find(d => d.id === gId && d.state === 'online'), 20000);
  ok(hSees && hSees.route === 'relay', '另一台输入带地址的连接码就连上了（不用先设置中转）', hSees && JSON.stringify(hSees));
  ok((await state(H)).relay.url === gs.relay.url && (await state(H)).relay.mode === 'custom', '它以后也经这个临时中转连');
  const t6 = '临时中转 ' + Date.now(); await test(H, 'copy', { kind: 'text', text: t6 });
  ok(await until(async () => (await test(G, 'clip')).text === t6, 10000), '同步照常');
  await test(G, 'tunnel-rotate', {});
  const g2 = await until(async () => { const s = await state(G); return s.relay.temp.state === 'ok' && s.relay.state === 'ok' && s.relay.url !== gs.relay.url ? s : null; }, 20000);
  ok(g2, '隧道地址变了（模拟重启），这台换到新地址');
  ok(await until(async () => (await state(G)).feed.find(f => f.note && f.note.includes('地址变了')), 5000), '提醒：别处的电脑要输入新的连接码');
  const newPort = g2 && new URL(g2.relay.url).port;
  const ct2 = await until(async () => { const t = (await state(G)).relay.codeText; return t && t.endsWith(':' + newPort) ? t : null; }, 15000);
  ok(ct2, '自动准备好新的连接码：' + ct2);
  ok(await until(async () => (await state(H)).devices.find(d => d.id === gId && d.state === 'offline'), 70000), '另一台经旧地址连不上了（显示离线）');
  await local(H, 'connect-code', { code: ct2 });
  ok(await until(async () => (await state(H)).relay.url === g2.relay.url && (await state(H)).devices.find(d => d.id === gId && d.state === 'online'), 30000), '输入新连接码，不用重新配对、不用核对就又连上了');
  const t7 = '换地址后 ' + Date.now(); await test(G, 'copy', { kind: 'text', text: t7 });
  ok(await until(async () => (await test(H, 'clip')).text === t7, 10000), '同步照常');
  await local(G, 'settings', { relay: '' });
  ok(await until(async () => { const s = await state(G); return s.relay.mode === 'off' && s.relay.temp.state === 'off'; }, 10000), '关掉跨网络，临时中转也停了');
}

main().catch(e => { failed++; console.log('出错：' + e.stack); }).finally(() => {
  for (const p of procs) try { p.kill(); } catch { }
  console.log(`\n合计：${passed} 项通过，${failed} 项失败`);
  setTimeout(() => process.exit(failed ? 1 : 0), 300);
});
