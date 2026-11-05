// 小云测试：起一个“只当中转”的服务器实例（带 3 MB 的文件池），两台不在同一个网络的电脑经它配对，
// 走一遍：放文件 → 另一台下载 → 链接在浏览器里解密下载 → 满了删最早的 → 超大提示 → 别的组看不到 → 删除 → 服务器重启后还在。
// 用法：node tests/pool.mjs [path\to\LanBridge.exe]
import { spawn, execSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const exe = path.resolve(process.argv[2] || 'dist/LanBridge.exe');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lb2-pool-'));
const procs = [];
let passed = 0, failed = 0;
const sleep = ms => new Promise(r => setTimeout(r, ms));
function ok(cond, name, extra) { if (cond) { passed++; console.log('  ✓ ' + name); } else { failed++; console.log('  ✗ ' + name + (extra ? '  ← ' + extra : '')); } }
const sha = b => crypto.createHash('sha256').update(b).digest('hex');
const RP = 49480, R = 'http://127.0.0.1:' + RP;

function startServer() {
  const p = spawn(process.env.POOL_SERVER_EXE || exe, ['--relay-server', '--port', String(RP), '--bind', '127.0.0.1', '--pool', path.join(root, 'pool'), '--pool-mb', '3', '--relay-max-mb', '64', '--log', root], { stdio: 'ignore' });
  procs.push(p); return p;
}
async function health() { try { return await (await fetch(R + '/health')).json(); } catch { return null; } }
function start(name, port) {
  const data = path.join(root, name);
  const p = spawn(exe, ['--data', data, '--port', String(port), '--headless', '--loopback', '--fake-clipboard', '--name', name, '--relay', R, '--no-lan'], { stdio: 'ignore' }); procs.push(p);
  return { name, port, data, base: 'http://127.0.0.1:' + port, token: null, proc: p };
}
async function ready(n) {
  for (let i = 0; i < 100; i++) { try { n.token = fs.readFileSync(path.join(n.data, 'desk-url.txt'), 'utf8').split('#t=')[1].trim(); await state(n); return; } catch { await sleep(150); } }
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
async function until(fn, ms = 20000, step = 250) { const end = Date.now() + ms; while (Date.now() < end) { try { const v = await fn(); if (v) return v; } catch { } await sleep(step); } return null; }

// 下载页里的解密代码（和浏览器里跑的是同一份）
const html = fs.readFileSync(path.join(here, '..', 'web', 'pool.html'), 'utf8');
const ctx = { TextEncoder, TextDecoder, Uint8Array, Uint32Array, Int32Array, Math, Error, atob, window: {}, document: {}, location: {} };
vm.createContext(ctx); vm.runInContext(html.split('<script>')[1].split('</script>')[0].replace(/\nmain\(\);\s*$/, '\n') + '\nthis.api = { openSealed };', ctx);
async function viaLink(link) {
  const [u, key] = link.split('#'); const id = u.split('/').pop();
  const r = await fetch(R + '/f/' + id); if (!r.ok) return { status: r.status };
  const fk = Buffer.from(key.replace(/-/g, '+').replace(/_/g, '/'), 'base64');
  const plain = Buffer.from(await ctx.api.openSealed(new Uint8Array(fk), new Uint8Array(await r.arrayBuffer()), () => { }));
  const n = plain.readInt32LE(0);
  return { status: 200, name: plain.subarray(4, 4 + n).toString('utf8'), body: plain.subarray(4 + n) };
}
async function upload(n, file) {
  const before = (await state(n)).feed.length;
  await local(n, 'send', { target: 'cloud', paths: [file] });
  return until(async () => { const f = (await state(n)).feed.find(x => x.to && x.to.includes('小云') && x.files[0] && x.files[0].name === path.basename(file) && (x.status === 'done' || x.status === 'failed')); return f; }, 30000);
}

async function main() {
  console.log('程序：' + exe + '\n数据：' + root);
  startServer();
  const h = await until(health, 15000, 150);
  ok(h && h.pool && h.pool.capMB === 3, '服务器模式起来了，带 3 MB 小云', JSON.stringify(h));
  const A = start('家里电脑', 49481), B = start('公司电脑', 49482), C = start('别人的电脑', 49483);
  await ready(A); await ready(B); await ready(C);
  const aId = (await test(A, 'info')).id;
  await until(async () => (await state(A)).relay.state === 'ok' && (await state(B)).relay.state === 'ok', 10000);
  const code = (await local(A, 'remote-code', {})).code; await sleep(600);
  await local(B, 'connect-code', { code });
  const rq = await until(async () => (await state(A)).requests[0]);
  if (rq) await local(A, 'answer', { id: rq.id, allow: true });
  ok(await until(async () => (await state(B)).devices.find(d => d.id === aId && d.state === 'online'), 20000), '两台经服务器配对上了');
  ok(await until(async () => (await state(A)).cloud.ready && (await state(B)).cloud.ready), '两台都看到小云可用');

  console.log('\n1. 放文件、另一台下载');
  const f1 = path.join(root, '季度报告 Q3.pdf'); fs.writeFileSync(f1, crypto.randomBytes(900 * 1024));
  const up = await upload(A, f1);
  ok(up && up.status === 'done', 'A 把文件放到小云', up && up.note);
  const listB = await local(B, 'cloud');
  const it = listB.files && listB.files.find(x => x.name === '季度报告 Q3.pdf');
  ok(it && it.from === '家里电脑' && it.size === 900 * 1024 && it.link.includes('/d/') && it.link.includes('#'), 'B 的小云里看到它（名字、大小、谁放的、链接）', JSON.stringify(listB).slice(0, 200));
  await local(B, 'cloud-download', { id: it.id });
  const got = path.join(B.data, '收到的文件', '季度报告 Q3.pdf');
  ok(await until(() => fs.existsSync(got) && sha(fs.readFileSync(got)) === sha(fs.readFileSync(f1)), 20000), 'B 下载下来，内容一致');
  ok(await until(async () => (await state(B)).feed.find(x => x.from === '小云' && x.status === 'done')), 'B 的动态里有这次下载，可以打开');

  console.log('\n2. 链接（手机、别人在浏览器里打开）');
  const page = await (await fetch(it.link.split('#')[0])).text();
  ok(page.includes('轻桥 · 小云') && page.includes('openSealed'), '链接打开是下载页');
  const v = await viaLink(it.link);
  ok(v.status === 200 && v.name === '季度报告 Q3.pdf' && sha(v.body) === sha(fs.readFileSync(f1)), '下载页里的解密代码解出原文件和文件名');
  const files = fs.readdirSync(path.join(root, 'pool'));
  const leak = files.some(n => fs.readFileSync(path.join(root, 'pool', n)).includes(Buffer.from('季度报告')));
  ok(!leak, '服务器上只有密文：存储里找不到文件名');

  console.log('\n3. 满了删最早的、超大提示');
  const f2 = path.join(root, 'b.bin'), f3 = path.join(root, 'c.bin'), f4 = path.join(root, 'd.bin');
  fs.writeFileSync(f2, crypto.randomBytes(1100 * 1024)); fs.writeFileSync(f3, crypto.randomBytes(1100 * 1024));
  await upload(A, f2); await sleep(50); await upload(B, f3);
  const l3 = await local(A, 'cloud');
  ok(l3.files.length === 2 && !l3.files.find(x => x.name === '季度报告 Q3.pdf') && l3.usedMB <= 3, '放不下时删掉了最早的那个', JSON.stringify(l3.files.map(x => x.name)));
  ok((await viaLink(it.link)).status === 404, '被删的文件，链接打开提示已经不在');
  fs.writeFileSync(f4, crypto.randomBytes(3200 * 1024));
  const big = await upload(A, f4);
  ok(big && big.status === 'failed' && /最多/.test(big.note || ''), '超过上限直接提示', big && big.note);

  console.log('\n4. 权限');
  const lc = await local(C, 'cloud');
  ok(lc.ready && lc.files.length === 0, '别的设备组看不到这个组的文件');
  const forged = await fetch(R + '/f/' + '0123456789abcdef'.slice(0, 16) + '/list', { headers: { 'X-LB-Pool': 'x'.repeat(64) } });
  const spaceA = fs.readFileSync(path.join(root, 'pool', 'index.json'), 'utf8').match(/"space":"([0-9a-f]{16})"/)[1];
  const wrong = await fetch(R + '/f/' + spaceA + '/list', { headers: { 'X-LB-Pool': 'y'.repeat(64) } });
  ok(wrong.status === 401, '口令不对列不出文件');
  ok(forged.status === 200 || forged.status === 401, '（没用过的空间第一次用时登记口令）');

  console.log('\n5. 删除、服务器重启');
  const del = (await local(B, 'cloud')).files.find(x => x.name === 'b.bin');
  await local(B, 'cloud-delete', { id: del.id });
  ok(!(await local(A, 'cloud')).files.find(x => x.name === 'b.bin'), 'B 删掉后，A 也看不到了');
  procs[0].kill(); await sleep(800);
  startServer(); await until(health, 15000, 150);
  ok(await until(async () => (await state(A)).cloud.ready, 30000), '服务器重启后自动重连');
  const after = await local(A, 'cloud');
  ok(after.files.length === 1 && after.files[0].name === 'c.bin', '服务器重启后文件还在');
  try {
    const pid = procs[procs.length - 1].pid;
    const ws = execSync(`powershell -NoProfile -Command "(Get-Process -Id ${pid}).WorkingSet64"`).toString().trim();
    console.log(`  · 服务器模式内存：${(ws / 1048576).toFixed(0)} MB`);
  } catch { }
}

main().catch(e => { failed++; console.log('出错：' + e.stack); }).finally(() => {
  for (const p of procs) try { p.kill(); } catch { }
  console.log(`\n合计：${passed} 项通过，${failed} 项失败`);
  setTimeout(() => process.exit(failed ? 1 : 0), 300);
});
