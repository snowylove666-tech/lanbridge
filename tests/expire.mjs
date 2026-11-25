// 小云按时间删除（2.2.25）：服务器 --pool-days 设得很短，放进去的文件到时间后列表里没了、也下载不到；重启后也照样清。
// 用法：node tests/expire.mjs [LanBridgeRelay.exe]
import { spawn } from 'node:child_process'; import fs from 'node:fs'; import os from 'node:os'; import path from 'node:path';
const relayExe = path.resolve(process.argv[2] || 'dist/LanBridgeRelay.exe');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lb-exp-')), RB = 'http://127.0.0.1:49496';
const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0, failed = 0, relay;
const ok = (c, n, x) => { if (c) { passed++; console.log('  ✓ ' + n); } else { failed++; console.log('  ✗ ' + n + (x ? '  ← ' + x : '')); } };
const start = days => { relay = spawn(relayExe, ['--port', '49496', '--bind', '127.0.0.1', '--pool', path.join(root, 'pool'), '--log', root, '--pool-days', String(days)], { stdio: 'ignore' }); return sleep(1000); };
const space = 'a'.repeat(16), H = { 'X-LB-Pool': 'b'.repeat(64) };
const put = () => fetch(RB + '/f/' + space, { method: 'POST', headers: { ...H, 'X-LB-Meta': 'bWV0YQ==', 'Content-Type': 'application/octet-stream' }, body: Buffer.alloc(1000, 1) }).then(r => r.json());
const list = () => fetch(RB + '/f/' + space + '/list', { headers: H }).then(r => r.json());
try {
  await start(3);
  const h = await (await fetch(RB + '/health')).json();
  ok(h.pool && h.pool.keepDays === 3, '默认（这里显式 3）留 3 天', JSON.stringify(h.pool));
  const a = await put();
  ok((await list()).files.some(f => f.id === a.id), '刚放的在列表里');
  relay.kill(); await sleep(800);
  await start(0.00003); // 约 2.6 秒
  ok((await list()).files.some(f => f.id === a.id), '没到时间还在');
  await sleep(3500);
  ok(!(await list()).files.some(f => f.id === a.id), '到时间后列表里没了');
  ok((await fetch(RB + '/f/' + a.id)).status === 404, '也下载不到了');
  ok(!fs.existsSync(path.join(root, 'pool', a.id + '.bin')), '磁盘上的文件删了');
  const b = await put(); relay.kill(); await sleep(3500);
  await start(0.00003);
  ok(!fs.existsSync(path.join(root, 'pool', b.id + '.bin')), '服务器关着时过期的，重启马上清掉');
} catch (e) { failed++; console.log('出错', e); }
finally { relay && relay.kill(); }
console.log(`\n合计：${passed} 项通过，${failed} 项失败`);
process.exit(failed ? 1 : 0);
