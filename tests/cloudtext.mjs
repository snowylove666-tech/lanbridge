// 小云存文字：手机/电脑存文字、长文字取全文、复制进剪贴板、下载页显示、服务器只有密文。用法：node tests/cloudtext.mjs [LanBridge.exe]
import { spawn } from 'node:child_process'; import fs from 'node:fs'; import os from 'node:os'; import path from 'node:path';
const exe = path.resolve(process.argv[2] || "dist/LanBridge.exe"), root = fs.mkdtempSync(path.join(os.tmpdir(), 'lb-2223-'));
const port = 49446, b = 'http://127.0.0.1:' + port, RB = 'http://127.0.0.1:49498';
const sleep = ms => new Promise(r => setTimeout(r, ms)); let passed = 0, failed = 0, ps = [];
const ok = (c, n, x) => { if (c) { passed++; console.log('  ✓ ' + n); } else { failed++; console.log('  ✗ ' + n + (x ? '  ← ' + x : '')); } };
try {
  ps.push(spawn(exe, ['--relay-server', '--port', '49498', '--bind', '127.0.0.1', '--pool', path.join(root, 'pool')], { stdio: 'ignore' })); await sleep(800);
  const data = path.join(root, 'pc');
  ps.push(spawn(exe, ['--data', data, '--port', String(port), '--headless', '--loopback', '--fake-clipboard', '--no-lan', '--name', '电脑', '--relay', RB], { stdio: 'ignore' }));
  let t; for (let i = 0; i < 100; i++) { try { t = fs.readFileSync(path.join(data, 'desk-url.txt'), 'utf8').split('#t=')[1].trim(); break; } catch { await sleep(150); } }
  const H = { 'X-LB-Local': t, 'Content-Type': 'application/json' };
  for (let i = 0; i < 40; i++) { const s = await (await fetch(b + '/api/local/state', { headers: H })).json(); if (s.cloud && s.cloud.ready) break; await sleep(300); }
  const info = await (await fetch(b + '/api/test/info', { headers: H })).json();
  const pair = await (await fetch(b + '/api/phone/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ secret: info.pair, name: 'iPhone' }) })).json();
  const P = { 'X-LB-Token': pair.token, 'Content-Type': 'application/json' };
  const short = '会议室密码 8848\n第二行：明早 9 点';
  let r = await fetch(b + '/api/phone/cloud-text', { method: 'POST', headers: P, body: JSON.stringify({ text: short }) });
  ok(r.ok, '手机存短文字到小云', r.status + ' ' + await r.text());
  const long = '长文字开头。' + '这是一段很长的说明，'.repeat(400) + '结尾在这里';
  r = await fetch(b + '/api/phone/cloud-text', { method: 'POST', headers: P, body: JSON.stringify({ text: long }) });
  ok(r.ok, '手机存长文字（' + long.length + ' 字）', r.status);
  r = await fetch(b + '/api/phone/cloud-text', { method: 'POST', headers: P, body: JSON.stringify({ text: '   ' }) });
  ok(r.status === 400, '空文字被拒', r.status);
  let L = await (await fetch(b + '/api/phone/cloud', { headers: P })).json();
  const ts = L.files.filter(f => f.kind === 'text');
  const s1 = ts.find(f => f.text === short), l1 = ts.find(f => f.text && f.text.startsWith('长文字开头'));
  ok(s1 && s1.full && s1.from === 'iPhone', '列表里短文字带全文、来自 iPhone', JSON.stringify(ts.map(f => [f.text && f.text.slice(0, 10), f.full])));
  ok(l1 && !l1.full && l1.text.length === 1500, '长文字只带开头 1500 字');
  r = await (await fetch(b + '/api/phone/cloud-read?id=' + l1.id, { headers: P })).json();
  ok(r.text === long, '手机取长文字全文一致', (r.text || r.error || '').slice(-20));
  r = await fetch(b + '/api/local/cloud-text', { method: 'POST', headers: H, body: JSON.stringify({ text: '电脑存的文字' }) });
  ok(r.ok, '电脑存文字', r.status);
  r = await fetch(b + '/api/local/cloud-copy', { method: 'POST', headers: H, body: JSON.stringify({ id: l1.id }) });
  const clip = await (await fetch(b + '/api/test/clip', { headers: H })).json();
  ok(r.ok && clip.text === long, '电脑点复制：长文字全文进剪贴板', r.status + ' ' + (clip.text || '').slice(0, 10));
  L = await (await fetch(b + '/api/local/cloud', { headers: H })).json();
  const pc = L.files.find(f => f.text === '电脑存的文字');
  ok(pc && pc.from === '电脑' && pc.name.endsWith('.txt'), '电脑的列表里有，名字是 .txt', pc && pc.name);
  // 下载页：用链接里的钥匙解密，内容就是那段文字
  const page = await (await fetch(s1.link.split('#')[0])).text();
  ok(page.includes('小云里的文字') && page.includes('复制文字'), '下载页带文字显示和复制');
  const enc = Buffer.from(await (await fetch(RB + '/f/' + s1.id)).arrayBuffer());
  ok(enc.length > 32 && !enc.includes(Buffer.from('8848')), '服务器上是密文，看不到文字');
  const fl = await fetch(b + '/api/phone/cloud', { headers: P });
  ok(fl.ok, '文件列表接口照常');
} catch (e) { failed++; console.log('出错', e); }
finally { for (const p of ps) p.kill(); }
console.log(`\n合计：${passed} 项通过，${failed} 项失败`);
process.exit(failed ? 1 : 0);
