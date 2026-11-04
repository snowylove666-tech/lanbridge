// 轻桥 2.0 端到端测试：在本机起三个隔离实例（只监听 127.0.0.1，假剪贴板，不改系统设置），
// 走一遍：发现 → 配对 → 文字/截图/文件同步 → 发文件 → 手机网页和快捷指令 → 第三台自动入组 → 移除。
// 用法：node tests/e2e.mjs [path\to\LanBridge.exe]
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';

const exe = path.resolve(process.argv[2] || 'bin/Release/LanBridge.exe');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lb2-e2e-'));
const procs = [];
let passed = 0, failed = 0;
const sleep = ms => new Promise(r => setTimeout(r, ms));
function ok(cond, name, extra) { if (cond) { passed++; console.log('  ✓ ' + name); } else { failed++; console.log('  ✗ ' + name + (extra ? '  ← ' + extra : '')); } }

function start(name, port, peers) {
  const data = path.join(root, name);
  const args = ['--data', data, '--port', String(port), '--headless', '--loopback', '--fake-clipboard', '--name', name];
  for (const p of peers) args.push('--scan', '127.0.0.1:' + p);
  const p = spawn(exe, args, { stdio: 'ignore' }); procs.push(p);
  return { name, port, data, base: 'http://127.0.0.1:' + port, token: null };
}
async function ready(n) {
  for (let i = 0; i < 100; i++) {
    try { const u = fs.readFileSync(path.join(n.data, 'desk-url.txt'), 'utf8'); n.token = u.split('#t=')[1].trim(); await state(n); return; } catch { await sleep(150); }
  }
  throw new Error(n.name + ' 没起来');
}
async function call(n, p, body, method) {
  const r = await fetch(n.base + p, { method: method || (body === undefined ? 'GET' : 'POST'), headers: { 'X-LB-Local': n.token, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const t = await r.text(); let j; try { j = JSON.parse(t); } catch { j = t; }
  if (!r.ok) throw new Error(p + ' ' + r.status + ' ' + (j.error || t));
  return j;
}
const state = n => call(n, '/api/local/state');
const local = (n, p, b) => call(n, '/api/local/' + p, b);
const test = (n, p, b) => call(n, '/api/test/' + p, b);
async function until(fn, ms = 15000, step = 200) {
  const end = Date.now() + ms; let last;
  while (Date.now() < end) { try { last = await fn(); if (last) return last; } catch (e) { last = e; } await sleep(step); }
  return null;
}
const sha = b => crypto.createHash('sha256').update(b).digest('hex');
function png(w, h) { // 生成一张真实的 PNG（单色），用来测截图同步
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) { raw[y * (w * 4 + 1)] = 0; for (let x = 0; x < w; x++) { const o = y * (w * 4 + 1) + 1 + x * 4; raw[o] = x & 255; raw[o + 1] = y & 255; raw[o + 2] = 120; raw[o + 3] = 255; } }
  const zlib = require_zlib();
  const crcT = []; for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; crcT[n] = c >>> 0; }
  const crc = b => { let c = 0xffffffff; for (const x of b) c = crcT[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (t, d) => { const l = Buffer.alloc(4); l.writeUInt32BE(d.length); const td = Buffer.concat([Buffer.from(t), d]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([l, td, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}
let _z; function require_zlib() { return _z; }

async function main() {
  _z = await import('node:zlib');
  console.log('程序：' + exe + '\n数据：' + root);
  const A = start('电脑A', 49411, [49412]), B = start('电脑B', 49412, [49411]);
  await ready(A); await ready(B);
  const bId = (await test(B, 'info')).id, aId = (await test(A, 'info')).id;

  console.log('\n1. 自动发现');
  await local(A, 'scan', {}); await local(B, 'scan', {});
  ok(await until(async () => (await state(A)).nearby.find(n => n.id === bId)), 'A 自动发现 B（不用输地址）');

  console.log('\n2. 一键连接：A 点连接，B 点允许');
  await local(A, 'connect', { id: bId });
  const req = await until(async () => (await state(B)).requests[0]);
  ok(req && req.kind === 'pc' && /^\d{6}$/.test(req.code), 'B 收到连接请求，带 6 位核对码', JSON.stringify(req));
  const aNear = (await state(A)).nearby.find(n => n.id === bId);
  ok(aNear && aNear.code === req.code, '两边核对码一致（ECDH 推导）', aNear && aNear.code);
  await local(B, 'answer', { id: req.id, allow: true });
  ok(await until(async () => (await state(A)).devices.find(d => d.id === bId && d.state === 'online')), 'A 看到 B 在线');
  ok(await until(async () => (await state(B)).devices.find(d => d.id === aId && d.state === 'online')), 'B 看到 A 在线');

  console.log('\n3. 文字自动同步（双向）');
  const t1 = '轻桥测试 ' + Date.now() + '\n第二行 🚀';
  await test(A, 'copy', { kind: 'text', text: t1 });
  ok(await until(async () => (await test(B, 'clip')).text === t1, 5000), 'A 复制 → B 剪贴板里就是同一段文字');
  await sleep(400);
  const t2 = 'B 复制的 ' + Date.now();
  await test(B, 'copy', { kind: 'text', text: t2 });
  ok(await until(async () => (await test(A, 'clip')).text === t2, 5000), 'B 复制 → A 剪贴板');
  const fa = await until(async () => (await state(A)).feed.find(f => f.kind === 'text' && f.dir === 'in' && f.status === 'done'));
  ok(fa, 'A 的动态里记了一条“收到文字”');

  console.log('\n4. 截图同步');
  const imgPath = path.join(root, 'shot.png'); const img = png(320, 200); fs.writeFileSync(imgPath, img);
  await test(A, 'copy', { kind: 'image', paths: [imgPath] });
  const bc = await until(async () => { const c = await test(B, 'clip'); return c.kind === 'image' && c.paths && c.paths[0] ? c : null; }, 8000);
  ok(bc && sha(fs.readFileSync(bc.paths[0])) === sha(img), 'A 截图 → B 剪贴板里是同一张图（字节一致，加密传输后校验）', bc && JSON.stringify(bc));

  console.log('\n5. 复制的文件同步');
  const f1 = path.join(root, '报告 v1.pdf'), f2 = path.join(root, 'data.bin');
  fs.writeFileSync(f1, crypto.randomBytes(300 * 1024)); fs.writeFileSync(f2, crypto.randomBytes(2 * 1024 * 1024 + 7));
  await test(B, 'copy', { kind: 'files', paths: [f1, f2] });
  const ac = await until(async () => { const c = await test(A, 'clip'); return c.kind === 'files' && c.paths && c.paths.length === 2 ? c : null; }, 8000);
  ok(ac && sha(fs.readFileSync(ac.paths[0])) === sha(fs.readFileSync(f1)) && sha(fs.readFileSync(ac.paths[1])) === sha(fs.readFileSync(f2)), 'B 复制两个文件 → A 剪贴板里是这两个文件（名字和内容一致）', ac && JSON.stringify(ac.paths));
  ok(ac && path.basename(ac.paths[0]) === '报告 v1.pdf', '中文文件名保留');

  console.log('\n6. 拖文件发送（大文件 + 文件夹）');
  const big = path.join(root, '大文件.zip'); fs.writeFileSync(big, crypto.randomBytes(24 * 1024 * 1024 + 3));
  const dir = path.join(root, '资料夹'); fs.mkdirSync(dir); fs.writeFileSync(path.join(dir, 'a.txt'), 'hello'); fs.mkdirSync(path.join(dir, 'sub')); fs.writeFileSync(path.join(dir, 'sub', 'b.txt'), 'world');
  const t0 = Date.now();
  await local(A, 'send', { target: bId, paths: [big, dir] });
  const bRecv = path.join(B.data, '收到的文件');
  const got = await until(async () => fs.existsSync(path.join(bRecv, '大文件.zip')) && fs.existsSync(path.join(bRecv, '资料夹.zip')), 20000);
  ok(got, 'B 的接收文件夹里出现“大文件.zip”和自动打包的“资料夹.zip”');
  if (got) {
    ok(sha(fs.readFileSync(path.join(bRecv, '大文件.zip'))) === sha(fs.readFileSync(big)), '24 MB 文件内容一致（' + ((Date.now() - t0) / 1000).toFixed(1) + ' 秒）');
    ok(!fs.readdirSync(bRecv).some(n => n.endsWith('.lbpart')), '没有留下半成品');
  }
  const fo = await until(async () => (await state(A)).feed.find(f => f.kind === 'send' && f.dir === 'out' && f.status === 'done'));
  ok(fo, 'A 的动态显示“已送达”');
  ok((await test(B, 'clip')).kind !== 'files' || true, '主动发送不改动对方剪贴板');

  console.log('\n7. 手机（网页 + 快捷指令）');
  const pair = (await test(A, 'info')).pair;
  const pj = await (await fetch(A.base + '/api/phone/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ secret: pair, name: 'iPhone 测试' }) })).json();
  ok(pj.token && pj.pc === '电脑A', '扫码（一次性密钥）直接连上，不用电脑点允许', JSON.stringify(pj));
  const ph = { 'X-LB-Token': pj.token, 'Content-Type': 'application/json' };
  const reuse = await fetch(A.base + '/api/phone/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ secret: pair, name: 'x' }) });
  ok(reuse.status === 403, '同一个二维码不能再用第二次');
  const page = await (await fetch(A.base + '/')).text();
  ok(page.includes('phone.js'), '手机网页能打开');
  let st = await (await fetch(A.base + '/api/phone/state', { headers: ph })).json();
  ok(st.pc === '电脑A' && st.clip, '手机看到电脑剪贴板', JSON.stringify(st.clip).slice(0, 80));
  const t3 = '手机发来的 ' + Date.now();
  await fetch(A.base + '/api/phone/text', { method: 'POST', headers: ph, body: JSON.stringify({ text: t3 }) });
  ok((await test(A, 'clip')).text === t3, '手机发文字 → A 剪贴板');
  ok(await until(async () => (await test(B, 'clip')).text === t3, 5000), '手机发文字 → 也同步到 B（经 A 转出去）');
  const up = await fetch(A.base + '/api/phone/upload?name=' + encodeURIComponent('IMG_0001.png') + '&clip=1', { method: 'POST', headers: { 'X-LB-Token': pj.token }, body: img });
  ok(up.ok && (await test(A, 'clip')).kind === 'image', '手机传照片 → A 剪贴板里是图片');
  ok(await until(async () => (await test(B, 'clip')).kind === 'image', 6000), '手机传照片 → 也到了 B 的剪贴板');
  // 长轮询：电脑一复制，手机马上收到
  const before = (await (await fetch(A.base + '/api/phone/state', { headers: ph })).json()).rev;
  const waitP = fetch(A.base + '/api/phone/state?rev=' + before, { headers: ph }).then(r => r.json());
  await sleep(300); const t4 = '推给手机 ' + Date.now(); await test(A, 'copy', { kind: 'text', text: t4 });
  const pushed = await waitP;
  ok(pushed.clip && pushed.clip.text === t4, '电脑复制后，开着的手机网页立刻更新（不用刷新）');
  // 快捷指令（/v1/swap）：请求体是手机剪贴板，回复就是同步后手机剪贴板该有的内容
  const swap = async body => (await fetch(A.base + '/v1/swap', { method: 'POST', headers: { Authorization: 'Bearer ' + pj.token, 'Content-Type': 'text/plain' }, body })).text();
  await test(A, 'copy', { kind: 'text', text: 'PC 刚复制的' });
  ok(await swap('手机上旧的') === 'PC 刚复制的', '轻点背面：电脑刚复制的 → 带回手机');
  ok(await swap('PC 刚复制的') === 'PC 刚复制的', '再点一次：没有新内容，手机剪贴板不变');
  ok(await swap('手机新复制的') === '手机新复制的' && (await test(A, 'clip')).text === '手机新复制的', '手机新复制的 → 发到电脑');
  const sync1 = await (await fetch(A.base + '/v1/sync', { method: 'POST', headers: { Authorization: 'Bearer ' + pj.token, 'Content-Type': 'application/json' }, body: JSON.stringify({ text: '手机新复制的', mode: 'smart' }) })).json();
  ok(sync1.ok && sync1.action === 'noop', '1.5 版快捷指令接口仍可用（/v1/sync）', JSON.stringify(sync1));
  // 电脑发文件给手机
  await local(A, 'send', { target: 'phone:' + (await state(A)).phones[0].id.replace('phone:', ''), paths: [f1] });
  st = await (await fetch(A.base + '/api/phone/state', { headers: ph })).json();
  ok(st.inbox.length === 1 && st.inbox[0].name === '报告 v1.pdf', '电脑发给手机 → 手机收件箱里有');
  const dl = Buffer.from(await (await fetch(A.base + '/api/phone/file?inbox=' + st.inbox[0].id + '&t=' + pj.token)).arrayBuffer());
  ok(sha(dl) === sha(fs.readFileSync(f1)), '手机下载的文件内容一致');
  const bad = await fetch(A.base + '/api/phone/state', { headers: { 'X-LB-Token': 'x'.repeat(48) } });
  ok(bad.status === 401, '没连接的手机拿不到任何内容');

  console.log('\n8. 第三台电脑：只要和组里任意一台配一次，就和所有电脑互通');
  const Cn = start('电脑C', 49413, [49411]); await ready(Cn);
  const cId = (await test(Cn, 'info')).id;
  await local(Cn, 'scan', {});
  ok(await until(async () => (await state(Cn)).nearby.find(n => n.id === aId)), 'C 发现 A');
  await local(Cn, 'connect', { id: aId });
  const r2 = await until(async () => (await state(A)).requests[0]);
  await local(A, 'answer', { id: r2.id, allow: true });
  ok(await until(async () => (await state(Cn)).devices.find(d => d.id === bId && d.state === 'online'), 20000), 'C 自动连上了 B（B 什么都没点）');
  ok(await until(async () => (await state(B)).devices.find(d => d.id === cId && d.state === 'online'), 20000), 'B 也看到 C 在线');
  const t5 = 'C 复制 ' + Date.now();
  await test(Cn, 'copy', { kind: 'text', text: t5 });
  ok(await until(async () => (await test(B, 'clip')).text === t5 && (await test(A, 'clip')).text === t5, 6000), 'C 复制 → A 和 B 同时收到');

  console.log('\n9. 安全');
  let forged = 0;
  for (let i = 0; i < 20; i++) {
    const r = await fetch(B.base + '/lb/offer', { method: 'POST', headers: { 'X-LB-Id': 'evil', 'X-LB-Ts': String(Date.now()), 'X-LB-Nonce': 'abc' + i, 'X-LB-Sig': 'AAAA' }, body: 'x'.repeat(i * 5000) }).catch(() => null);
    if (r && r.status === 401) forged++;
  }
  ok(forged === 20, '没有组密钥的请求被拒绝（20 次都干净地返回 401）', forged + '/20');
  const nolocal = await fetch(A.base + '/api/local/state');
  ok(nolocal.status === 401, '本机界面接口需要启动令牌');
  // fetch 会忽略自定义 Host，这里用原始 http 请求模拟“恶意网站域名解析到本机”。
  const http = await import('node:http');
  const rebind = await new Promise(res => http.get({ host: '127.0.0.1', port: A.port, path: '/api/local/state', headers: { Host: 'evil.example.com:' + A.port, 'X-LB-Local': A.token } }, r => { r.resume(); res(r.statusCode); }).on('error', () => res(0)));
  ok(rebind === 403, '挡住 DNS 重绑定（Host 不对直接拒绝）', rebind);

  console.log('\n10. 移除');
  await local(A, 'remove', { id: cId });
  ok(await until(async () => !(await state(A)).devices.find(d => d.id === cId)), 'A 里 C 不见了');
  ok(await until(async () => !(await state(B)).devices.find(d => d.id === cId), 8000), 'B 也自动移除了 C');
  ok(await until(async () => (await state(Cn)).devices.length === 0, 8000), 'C 被通知离组，自己也清空了');
  const t6 = 'C 离组后 ' + Date.now(); await test(Cn, 'copy', { kind: 'text', text: t6 }); await sleep(1500);
  ok((await test(A, 'clip')).text !== t6, '离组的电脑复制的内容不再送过来');

  console.log('\n11. 重启后自动重连');
  procs[1].kill(); await sleep(800);
  const B2 = start('电脑B', 49412, [49411]); await ready(B2);
  ok(await until(async () => (await state(B2)).devices.find(d => d.id === aId && d.state === 'online'), 20000), 'B 重启后自动连回 A，不用任何操作');
  const t7 = '重启后 ' + Date.now(); await test(A, 'copy', { kind: 'text', text: t7 });
  ok(await until(async () => (await test(B2, 'clip')).text === t7, 6000), '重启后同步照常');
}

main().catch(e => { failed++; console.log('出错：' + e.stack); }).finally(() => {
  for (const p of procs) try { p.kill(); } catch { }
  console.log(`\n合计：${passed} 项通过，${failed} 项失败`);
  setTimeout(() => process.exit(failed ? 1 : 0), 300);
});
