// 电脑关着时手机照样能用（2.2.24）：电脑在线时手机拿到小云通行证 → 电脑关掉 → 中转自己给手机网页 →
// 手机（这里用和网页同一份 lbcrypto.js）直接把文字、文件加密存进小云 → 电脑开机后收进“动态”。
// 用法：node tests/offline.mjs [LanBridge.exe] [LanBridgeRelay.exe]
import { spawn } from 'node:child_process'; import fs from 'node:fs'; import os from 'node:os'; import path from 'node:path';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const LBC = require('../web/lbcrypto.js');
globalThis.crypto ??= (await import('node:crypto')).webcrypto;
const exe = path.resolve(process.argv[2] || 'dist/LanBridge.exe'), relayExe = path.resolve(process.argv[3] || 'dist/LanBridgeRelay.exe');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lb-off-'));
const port = 49447, b = 'http://127.0.0.1:' + port, RB = 'http://127.0.0.1:49499';
const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0, failed = 0, relay, pc;
const ok = (c, n, x) => { if (c) { passed++; console.log('  ✓ ' + n); } else { failed++; console.log('  ✗ ' + n + (x ? '  ← ' + x : '')); } };
const data = path.join(root, 'pc');
const startPc = () => { try { fs.unlinkSync(path.join(data, 'desk-url.txt')); } catch { } return spawn(exe, ['--data', data, '--port', String(port), '--headless', '--loopback', '--fake-clipboard', '--no-lan', '--no-shell', '--name', '家里电脑', '--relay', RB], { stdio: 'ignore' }); };
async function local() {
  let t; for (let i = 0; i < 100; i++) { try { t = fs.readFileSync(path.join(data, 'desk-url.txt'), 'utf8').split('#t=')[1].trim(); break; } catch { await sleep(150); } }
  const H = { 'X-LB-Local': t, 'Content-Type': 'application/json' };
  for (let i = 0; i < 60; i++) { try { if ((await fetch(b + '/api/local/state', { headers: H })).ok) break; } catch { } await sleep(250); }
  return H;
}
const state = async H => (await fetch(b + '/api/local/state', { headers: H })).json();
try {
  relay = spawn(relayExe, ['--port', '49499', '--bind', '127.0.0.1', '--pool', path.join(root, 'pool'), '--log', path.join(root, 'rlog')], { stdio: 'ignore' }); await sleep(1000);
  pc = startPc(); let H = await local();
  for (let i = 0; i < 40; i++) { const s = await state(H); if (s.cloud && s.cloud.ready) break; await sleep(300); }
  const info = await (await fetch(b + '/api/test/info', { headers: H })).json();
  const pair = await (await fetch(b + '/api/phone/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ secret: info.pair, name: 'iPhone' }) })).json();
  const st = await (await fetch(b + '/api/phone/state', { headers: { 'X-LB-Token': pair.token } })).json();
  const M = st.relay;
  ok(st.pool && st.pool.space && st.pool.auth && st.pool.mk, '电脑在线：手机拿到小云通行证');
  // 电脑在线时放一条，离线时手机也要能读到
  await fetch(b + '/api/phone/cloud-text', { method: 'POST', headers: { 'X-LB-Token': pair.token, 'Content-Type': 'application/json' }, body: JSON.stringify({ text: '电脑开着时存的' }) });

  pc.kill(); await sleep(3000);
  let r = await fetch(M); const page = await r.text();
  ok(r.status === 200 && page.includes('lb-offline') && page.includes('lbcrypto.js'), '电脑关着：中转自己给手机网页', r.status);
  for (const f of ['phone.js', 'lbcrypto.js', 'phone.css', 'base.css', 'icons.js', 'icon.svg']) { r = await fetch(M + 'assets/' + f); await r.arrayBuffer(); if (!r.ok) ok(false, '网页文件 ' + f, r.status); }
  ok(true, '网页的脚本、样式都能拿到');

  // 手机直接读写小云（和 phone.js 里 poolList / poolPut 一样）
  const p = st.pool, mk = LBC.unb64(p.mk);
  const list = async () => { const d = await (await fetch(RB + '/f/' + p.space + '/list', { headers: { 'X-LB-Pool': p.auth } })).json(); return d.files.map(f => ({ id: f.id, m: JSON.parse(LBC.td.decode(LBC.open(mk, LBC.unb64(f.meta)))) })); };
  let L = await list();
  ok(L.some(x => x.m.text === '电脑开着时存的'), '电脑关着：手机能看到小云里电脑开着时存的文字');
  async function put(name, bytes, extra) {
    const fk = LBC.random(32), nb = LBC.te.encode(name), plain = new Uint8Array(4 + nb.length + bytes.length);
    new DataView(plain.buffer).setInt32(0, nb.length, true); plain.set(nb, 4); plain.set(bytes, 4 + nb.length);
    const meta = Object.assign({ v: 1, name, size: bytes.length, from: 'iPhone', key: LBC.b64url(fk), inbox: '1' }, extra || {});
    return fetch(RB + '/f/' + p.space, { method: 'POST', headers: { 'X-LB-Pool': p.auth, 'X-LB-Meta': LBC.b64(LBC.seal(mk, LBC.te.encode(JSON.stringify(meta)))), 'Content-Type': 'application/octet-stream' }, body: LBC.seal(fk, plain) });
  }
  const msg = '电脑关着时手机发的：明天 10 点开会';
  r = await put('文字 明天.txt', LBC.te.encode(msg), { kind: 'text', text: msg, full: '1' });
  ok(r.ok, '电脑关着：手机存文字进小云', r.status);
  const photo = LBC.random(200000);
  r = await put('IMG_0007.jpg', photo);
  ok(r.ok, '电脑关着：手机存照片进小云', r.status);
  r = await fetch(RB + '/f/' + p.space + '/list', { headers: { 'X-LB-Pool': 'x'.repeat(64) } });
  ok(r.status === 401, '口令不对的拿不到列表', r.status);
  L = await list();
  const ph = L.find(x => x.m.name === 'IMG_0007.jpg');
  const enc = new Uint8Array(await (await fetch(RB + '/f/' + ph.id)).arrayBuffer());
  const plain = LBC.open(LBC.unb64(ph.m.key), enc);
  ok(Buffer.from(plain.subarray(4 + 12)).equals(Buffer.from(photo)), '手机能把照片取回来，内容一致');

  // 电脑开机
  pc = startPc(); H = await local();
  let got = null;
  for (let i = 0; i < 60; i++) { const s = await state(H); const f = (s.feed || []).find(x => x.text === msg); if (f) { got = s; break; } await sleep(500); }
  ok(got, '电脑开机后：手机离线时发的文字出现在动态里');
  ok(got && (got.feed || []).some(x => (x.note || '').includes('IMG_0007.jpg')), '照片也在动态里提示（在小云里下载）');
  const id = got && got.feed.find(x => x.text === msg).id;
  r = await fetch(b + '/api/local/recopy', { method: 'POST', headers: H, body: JSON.stringify({ id }) });
  const clip = await (await fetch(b + '/api/test/clip', { headers: H })).json();
  ok(clip.text === msg, '在动态里点“再复制一次”进剪贴板', r.status + ' ' + (clip.text || '').slice(0, 20));
  // 重启不重复收
  pc.kill(); await sleep(1500); pc = startPc(); H = await local(); await sleep(6000);
  const s2 = await state(H);
  ok((s2.feed || []).filter(x => x.text === msg).length === 1, '再开一次机不会重复收');
  r = await fetch(M); await r.text();
  ok(r.ok, '电脑在线时网页照常');
} catch (e) { failed++; console.log('出错', e); }
finally { pc && pc.kill(); relay && relay.kill(); }
console.log(`\n合计：${passed} 项通过，${failed} 项失败`);
process.exit(failed ? 1 : 0);
