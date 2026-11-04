// 真实剪贴板 + 真实窗口测试：一个真实实例（读写系统剪贴板、打开 WebView2 窗口），配对一个假剪贴板实例。
// 会先保存当前剪贴板文字，测完还原。只监听 127.0.0.1，不改开机启动和右键菜单。
// node tests/realclip.mjs <exe> <对方端口> <对方令牌>
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs'; import os from 'node:os'; import path from 'node:path'; import crypto from 'node:crypto';
const [exe, peerPort, peerToken] = process.argv.slice(2);
const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0, failed = 0;
const ok = (c, n, x) => { if (c) { passed++; console.log('  ✓ ' + n); } else { failed++; console.log('  ✗ ' + n + (x ? '  ← ' + x : '')); } };
const ps = cmd => execFileSync('powershell.exe', ['-NoProfile', '-STA', '-Command', '[Console]::OutputEncoding=[Text.Encoding]::UTF8;' + cmd], { encoding: 'utf8' }).trim();
const data = fs.mkdtempSync(path.join(os.tmpdir(), 'lb2-real-'));
const P = { base: 'http://127.0.0.1:' + peerPort, token: peerToken };
const R = { base: 'http://127.0.0.1:49450' };
async function call(n, p, body) { const r = await fetch(n.base + p, { method: body === undefined ? 'GET' : 'POST', headers: { 'X-LB-Local': n.token, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) }); const j = await r.json(); if (!r.ok) throw new Error(p + ' ' + j.error); return j; }
async function until(fn, ms = 8000) { const end = Date.now() + ms; while (Date.now() < end) { try { const v = await fn(); if (v) return v; } catch { } await sleep(200); } return null; }

const saved = ps('Get-Clipboard -Raw');
const proc = spawn(exe, ['--data', data, '--port', '49450', '--loopback', '--name', '真机测试', '--scan', '127.0.0.1:' + peerPort], { stdio: 'ignore' });
try {
  for (let i = 0; i < 60 && !R.token; i++) { try { R.token = fs.readFileSync(path.join(data, 'desk-url.txt'), 'utf8').split('#t=')[1].trim(); } catch { await sleep(200); } }
  const pid = (await call(P, '/api/test/info')).id, rid = (await call(R, '/api/local/state')).me.id;
  await call(R, '/api/local/scan', {});
  await until(async () => (await call(R, '/api/local/state')).nearby.find(n => n.id === pid));
  await call(R, '/api/local/connect', { id: pid });
  const req = await until(async () => (await call(P, '/api/local/state')).requests.find(r => r.name === '真机测试'));
  await call(P, '/api/local/answer', { id: req.id, allow: true });
  ok(await until(async () => (await call(R, '/api/local/state')).devices.find(d => d.id === pid && d.state === 'online'), 15000), '真实实例和对方连上');

  console.log('\n真实窗口');
  await sleep(2500);
  const shot = path.join(data, 'window.png');
  ps(`Add-Type -AssemblyName System.Drawing; Add-Type @'
using System;using System.Runtime.InteropServices;using System.Drawing;
public class W{[DllImport("user32.dll")]public static extern bool SetProcessDPIAware();[DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out R r);[DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);
public struct R{public int L,T,Ri,B;}
public static string Shot(IntPtr h,string p){SetProcessDPIAware();R r;GetWindowRect(h,out r);var b=new Bitmap(r.Ri-r.L,r.B-r.T);using(var g=Graphics.FromImage(b)){var dc=g.GetHdc();PrintWindow(h,dc,2);g.ReleaseHdc(dc);}b.Save(p);return (r.Ri-r.L)+"x"+(r.B-r.T);}}
'@ -ReferencedAssemblies System.Drawing; $p=Get-Process -Id ${proc.pid}; [W]::Shot($p.MainWindowHandle,'${shot.replace(/\\/g, '\\\\')}')`);
  ok(fs.existsSync(shot) && fs.statSync(shot).size > 20000, 'WebView2 窗口打开并截图：' + shot);

  console.log('\n真实剪贴板');
  const t1 = '轻桥真机测试 ' + crypto.randomBytes(4).toString('hex') + ' 中文✓';
  ps(`Set-Clipboard -Value '${t1}'`);
  const got1 = await until(async () => (await call(P, '/api/test/clip')).text === t1);
  ok(got1, '在本机复制文字 → 对方收到', got1 ? '' : JSON.stringify({ peerClip: (await call(P, '/api/test/clip')).text, myFeed: (await call(R, '/api/local/state')).feed.slice(0, 2).map(f => [f.kind, f.dir, f.status, f.note, f.text]), localClip: ps('Get-Clipboard -Raw') }));
  const t2 = '对方发来 ' + crypto.randomBytes(4).toString('hex') + ' 第二行\r\n结束';
  await call(P, '/api/test/copy', { kind: 'text', text: t2 });
  ok(await until(async () => ps('Get-Clipboard -Raw') === t2.trim()), '对方复制 → 本机剪贴板直接能粘贴', ps('Get-Clipboard -Raw'));
  await sleep(1200);
  ok((await call(P, '/api/test/clip')).text === t2, '写进本机剪贴板后不会再回传（不打乒乓）');

  ps(`Add-Type -AssemblyName System.Windows.Forms,System.Drawing; $b=New-Object System.Drawing.Bitmap 300,180; $g=[System.Drawing.Graphics]::FromImage($b); $g.Clear([System.Drawing.Color]::SteelBlue); $g.FillEllipse([System.Drawing.Brushes]::Gold,40,30,120,120); [System.Windows.Forms.Clipboard]::SetImage($b)`);
  const img = await until(async () => { const c = await call(P, '/api/test/clip'); return c.kind === 'image' && c.paths && fs.existsSync(c.paths[0]) ? c : null; });
  ok(img && fs.statSync(img.paths[0]).size > 500, '在本机截图（剪贴板图片）→ 对方收到 PNG', img && img.paths[0]);

  const f = path.join(data, '真机测试文件.txt'); fs.writeFileSync(f, '你好，轻桥 ' + Date.now());
  ps(`Set-Clipboard -Path '${f}'`);
  const fc = await until(async () => { const c = await call(P, '/api/test/clip'); return c.kind === 'files' && c.paths && c.paths[0] && c.paths[0].endsWith('真机测试文件.txt') ? c : null; });
  ok(fc && fs.readFileSync(fc.paths[0], 'utf8') === fs.readFileSync(f, 'utf8'), '在资源管理器复制文件 → 对方剪贴板是同一个文件');

  if (img) {
    await call(P, '/api/test/copy', { kind: 'image', paths: [img.paths[0]] });
    ok(await until(async () => ps('Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Clipboard]::ContainsImage()') === 'True'), '对方截图 → 本机剪贴板是图片（能直接贴进微信/文档）');
  }
  await call(P, '/api/test/copy', { kind: 'files', paths: [f] });
  ok(await until(async () => ps('(Get-Clipboard -Format FileDropList | Select-Object -First 1).Name') === '真机测试文件.txt'), '对方复制文件 → 本机资源管理器里能直接粘贴');
} catch (e) { failed++; console.log('出错：' + e.stack); }
finally {
  try { ps(saved ? `Set-Clipboard -Value @'\n${saved}\n'@` : 'Set-Clipboard -Value $null'); console.log('\n已还原你原来的剪贴板文字'); } catch { console.log('\n还原剪贴板失败'); }
  proc.kill();
  console.log(`合计：${passed} 项通过，${failed} 项失败`);
}
