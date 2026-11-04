// 起两个演示实例（A 已和 B 配对、连了一部手机、有一些动态），一直运行，用来看界面。
// node tests/demo.mjs [exe]   → 打印 A 的界面地址和手机地址
import { spawn } from 'node:child_process';
import fs from 'node:fs'; import os from 'node:os'; import path from 'node:path'; import crypto from 'node:crypto';
const exe = path.resolve(process.argv[2] || 'bin/Release/LanBridge.exe');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lb2-demo-'));
const sleep = ms => new Promise(r => setTimeout(r, ms));
const procs = [];
function start(name, port, peer, extra = []) {
  const data = path.join(root, name);
  procs.push(spawn(exe, ['--data', data, '--port', String(port), '--headless', '--loopback', '--fake-clipboard', '--name', name, '--scan', '127.0.0.1:' + peer, ...extra], { stdio: 'ignore' }));
  return { name, port, data, base: 'http://127.0.0.1:' + port };
}
async function ready(n) { for (;;) { try { n.token = fs.readFileSync(path.join(n.data, 'desk-url.txt'), 'utf8').split('#t=')[1].trim(); await call(n, '/api/local/state'); return; } catch { await sleep(150); } } }
async function call(n, p, body) { const r = await fetch(n.base + p, { method: body === undefined ? 'GET' : 'POST', headers: { 'X-LB-Local': n.token, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) }); const j = await r.json(); if (!r.ok) throw new Error(j.error); return j; }
async function until(fn) { for (let i = 0; i < 100; i++) { try { const v = await fn(); if (v) return v; } catch { } await sleep(200); } }
process.on('SIGINT', () => { procs.forEach(p => p.kill()); process.exit(0); });
async function png(w, h) { // 一张渐变“截图”
  const zlib = await import('node:zlib');
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const o = y * (w * 4 + 1) + 1 + x * 4; raw[o] = 60 + x * 120 / w; raw[o + 1] = 110 + y * 80 / h; raw[o + 2] = 230 - x * 60 / w; raw[o + 3] = 255; if (y > 40 && y < 60 && x > 40 && x < w - 200) raw.fill(255, o, o + 3); if (y > 80 && y < 280 && x > 40 && x < w - 40 && (x + y) % 3) raw.fill(245, o, o + 3); }
  const crcT = []; for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; crcT[n] = c >>> 0; }
  const crc = b => { let c = 0xffffffff; for (const x of b) c = crcT[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (t, d) => { const l = Buffer.alloc(4); l.writeUInt32BE(d.length); const td = Buffer.concat([Buffer.from(t), d]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([l, td, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}
process.on('exit', () => procs.forEach(p => p.kill()));

const mode = process.argv[3] || 'full';
const A = start('书房台式机', 49431, 49432), B = start('LAPTOP-出差本', 49432, 49431);
await ready(A); await ready(B);
if (mode === 'empty') {
  await call(A, '/api/local/scan', {}); await call(B, '/api/local/scan', {});
  console.log('DESK ' + A.base + '/desk#t=' + A.token); setInterval(() => { }, 1 << 30);
} else {
  const bId = (await call(B, '/api/test/info')).id;
  await call(A, '/api/local/scan', {});
  await until(async () => (await call(A, '/api/local/state')).nearby.find(n => n.id === bId));
  await call(A, '/api/local/connect', { id: bId });
  const req = await until(async () => (await call(B, '/api/local/state')).requests[0]);
  await call(B, '/api/local/answer', { id: req.id, allow: true });
  await until(async () => (await call(A, '/api/local/state')).devices.find(d => d.state === 'online'));
  const pair = (await call(A, '/api/test/info')).pair;
  const ph = await (await fetch(A.base + '/api/phone/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ secret: pair, name: 'iPhone 15' }) })).json();
  // 一些动态
  const dir = path.join(root, 'files'); fs.mkdirSync(dir);
  const shot = path.join(dir, 'shot.png');
  fs.writeFileSync(shot, await png(560, 320));
  const doc = path.join(dir, '2026 Q3 经营分析.pptx'); fs.writeFileSync(doc, crypto.randomBytes(1800 * 1024));
  const mov = path.join(dir, '会议录音.m4a'); fs.writeFileSync(mov, crypto.randomBytes(9 * 1024 * 1024));
  await call(B, '/api/test/copy', { kind: 'text', text: 'ssh deploy@10.0.3.21 -p 2222 && cd /srv/app && git pull' }); await sleep(600);
  await call(A, '/api/test/copy', { kind: 'image', paths: [shot] }); await sleep(600);
  await call(A, '/api/local/send', { target: bId, paths: [doc, mov] }); await sleep(800);
  await fetch(A.base + '/api/phone/text', { method: 'POST', headers: { 'X-LB-Token': ph.token, 'Content-Type': 'application/json' }, body: JSON.stringify({ text: '明天上午 10:00 和李总在国贸三期 B 座 21 层见面，带上合同第二版。' }) }); await sleep(500);
  await call(B, '/api/test/copy', { kind: 'text', text: '轻桥会把你复制的文字、截图和文件，自动带到你的其他设备上。\n不用选设备，不用点发送。' });
  await sleep(800);
  console.log('DESK ' + A.base + '/desk#t=' + A.token);
  console.log('PHONE ' + A.base + '/?t=' + ph.token + '#token');
  console.log('PHONETOKEN ' + ph.token);
  console.log('DESKB ' + B.base + '/desk#t=' + B.token);
  setInterval(() => { }, 1 << 30);
}
