// 轻桥中转服务：两台设备不在同一个网络时，经这里转一下。零依赖，Node 18+。
//
// 电脑上的轻桥一直挂着一个长轮询 GET /p/<设备ID>，等别处找它的请求。别处的请求发到：
//   /t/<设备ID>/lb/...   同组的另一台电脑（内容已用设备组密钥端到端加密，中转看不到明文）
//   /m/<设备ID>/...      手机网页和快捷指令（经 HTTPS 到这里）
//   /c/<配对码>/lb/join  第一次远程配对（配对码 8 位，电脑在线时有效）
// 中转把请求交给那台电脑：它用 GET /b/<请求ID> 取请求体，用 POST /r/<请求ID> 送回完整的 HTTP 回复，原样转给请求方。
// 中转不存任何内容，只在内存里转发；只记设备 ID 和它的轮询密钥哈希（防止别人冒充这台电脑收请求）。
//
// 环境变量：PORT（默认 49330）、HOST（默认 127.0.0.1，前面放 Caddy 做 HTTPS）、
//           MAX_BODY_MB（单个上传上限，默认 300）、STATE（设备密钥哈希存放文件，默认 ./relay-state.json）

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';

const PORT = +(process.env.PORT || 49330);
const HOST = process.env.HOST || '127.0.0.1';
const MAX_BODY = (+(process.env.MAX_BODY_MB || 300)) * 1024 * 1024;
const STATE = process.env.STATE || './relay-state.json';
const START_MS = 60000;   // 请求交给电脑后，多久内它要开始处理（长轮询最多 25 秒才回）
const POLL_MS = 25000, ONLINE_MS = 10000, CODE_MS = 120000;

const devices = new Map(); // id → { keyHash, seen, waiters: [res], queue: [item], code }
const codes = new Map();   // code → { id, until }
const pending = new Map(); // rid → { req, res, length, bodyTaken, replied, timer }
const stats = { started: Date.now(), requests: 0, up: 0, down: 0 };

const ID = /^[0-9a-f]{16}$/, CODE = /^[2-9A-HJ-NP-Z]{8}$/;
const sha = s => crypto.createHash('sha256').update(s).digest('hex');
const log = (...a) => console.log(new Date().toISOString().slice(0, 19), ...a);
const online = d => !!d && (d.waiters.length > 0 || Date.now() - d.seen < ONLINE_MS);
function json(res, status, obj) {
  if (res.headersSent || res.destroyed) return;
  const b = Buffer.from(JSON.stringify(obj));
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'content-length': b.length, 'cache-control': 'no-store' });
  res.end(b);
}

// ───── 设备密钥哈希：重启后仍只认原来那台电脑 ─────
let dirty = false;
try { for (const [id, v] of Object.entries(JSON.parse(fs.readFileSync(STATE, 'utf8')))) devices.set(id, { keyHash: v.k, seen: 0, last: v.s, waiters: [], queue: [], code: null }); } catch { }
function save() {
  if (!dirty) return; dirty = false;
  const o = {}; for (const [id, d] of devices) if (d.keyHash) o[id] = { k: d.keyHash, s: d.last || d.seen };
  try { fs.writeFileSync(STATE + '.tmp', JSON.stringify(o)); fs.renameSync(STATE + '.tmp', STATE); } catch (e) { log('保存失败', e.message); }
}

// ───── 电脑来取请求 ─────
function poll(req, res, id, q) {
  const k = q.get('k') || '';
  if (!ID.test(id) || k.length < 16 || k.length > 128) return json(res, 400, { error: '参数不对' });
  let d = devices.get(id);
  if (!d) { d = { keyHash: null, seen: 0, waiters: [], queue: [], code: null }; devices.set(id, d); }
  const kh = sha(k), now = Date.now();
  // 第一次见到这个设备 ID 时记下密钥哈希，之后只认这把；30 天没来过的才允许换。
  if (d.keyHash && d.keyHash !== kh && now - (d.last || d.seen) < 30 * 86400e3) return json(res, 403, { error: '这个设备 ID 已被另一台电脑使用' });
  if (d.keyHash !== kh) { d.keyHash = kh; dirty = true; }
  if (!d.last || now - d.last > 86400e3) dirty = true;
  d.seen = now; d.last = now;
  const code = (q.get('code') || '').toUpperCase();
  if (CODE.test(code)) {
    const c = codes.get(code);
    if (!c || c.id === id || c.until < now) {
      if (d.code && d.code !== code) codes.delete(d.code);
      d.code = code; codes.set(code, { id, until: now + CODE_MS });
    }
  } else if (d.code) { codes.delete(d.code); d.code = null; }
  // hi=1：电脑刚连上来，马上回一声，它就知道中转是通的。
  if (d.queue.length || q.get('hi')) return json(res, 200, { reqs: d.queue.splice(0, 32), max: MAX_BODY });
  const w = res;
  const drop = () => { clearTimeout(t); const i = d.waiters.indexOf(w); if (i >= 0) d.waiters.splice(i, 1); d.seen = Date.now(); };
  const t = setTimeout(() => { drop(); json(w, 200, { reqs: [], max: MAX_BODY }); }, POLL_MS);
  w._lbDrop = drop;
  w.on('close', drop);
  d.waiters.push(w);
}

function deliver(d, item) {
  const w = d.waiters.shift();
  if (w) { w._lbDrop(); json(w, 200, { reqs: [item], max: MAX_BODY }); return true; }
  if (d.queue.length >= 64) return false;
  d.queue.push(item); return true;
}

// ───── 别处找电脑 ─────
const PASS = new Set(['content-type', 'content-length', 'authorization', 'x-lb-token', 'x-lb-id', 'x-lb-ts', 'x-lb-nonce', 'x-lb-sig', 'x-lb-port', 'range', 'user-agent', 'origin', 'sec-fetch-site']);
function allowed(kind, method, path) {
  if (kind === 't') return path.startsWith('/lb/') && !path.startsWith('/lb/join');
  if (kind === 'c') return ['/lb/join', '/lb/join/poll', '/lb/hello'].includes(path);
  if (kind === 'm') return path.startsWith('/api/phone/') || ['/v1/swap', '/v1/sync', '/v1/ack'].includes(path)
    || ((method === 'GET' || method === 'HEAD') && (path === '/' || path.startsWith('/assets/') || path === '/apple-touch-icon.png' || path === '/favicon.ico'));
  return false;
}

function tunnel(req, res, kind, key, rest, search) {
  let id = key;
  if (kind === 'c') {
    const c = codes.get(key.toUpperCase().replace(/[^0-9A-Z]/g, ''));
    if (!c || c.until < Date.now()) return json(res, 404, { error: '配对码不对，或那台电脑已经关了轻桥' });
    id = c.id;
  } else if (!ID.test(id)) return json(res, 404, { error: '没有这台设备' });
  if (kind === 'm' && rest === '') { res.writeHead(301, { location: `/m/${id}/${search}` }); return res.end(); }
  if (!allowed(kind, req.method, rest)) return json(res, 403, { error: '不允许经中转访问这个地址' });
  const d = devices.get(id);
  if (!online(d)) return json(res, 503, { error: '那台电脑现在不在线（没开机，或没运行轻桥）' });
  if (req.headers['transfer-encoding'] && !req.headers['content-length']) return json(res, 411, { error: '需要 Content-Length' });
  const length = +(req.headers['content-length'] || 0);
  if (!Number.isSafeInteger(length) || length < 0) return json(res, 400, { error: '长度不对' });
  if (length > MAX_BODY) return json(res, 413, { error: `经中转一次最多传 ${MAX_BODY / 1048576} MB，大文件请在同一个网络里传` });
  const headers = {};
  for (const [k, v] of Object.entries(req.headers)) if (PASS.has(k)) headers[k] = v;
  const rid = crypto.randomBytes(12).toString('hex');
  const e = { req, res, length, bodyTaken: length === 0, replied: false, timer: null };
  pending.set(rid, e);
  if (!deliver(d, { rid, kind, method: req.method, target: rest + search, headers, length })) { pending.delete(rid); return json(res, 503, { error: '那台电脑太忙了，请稍后再试' }); }
  stats.requests++;
  e.timer = setTimeout(() => fail(rid, 504, '那台电脑没有响应'), START_MS);
  res.on('close', () => { if (pending.get(rid) === e && !e.replied) { clearTimeout(e.timer); pending.delete(rid); } });
}

function fail(rid, status, msg) {
  const e = pending.get(rid); if (!e) return;
  pending.delete(rid); clearTimeout(e.timer);
  json(e.res, status, { error: msg });
  if (!e.bodyTaken) e.req.resume();
}

// 电脑取请求体
function body(req, res, rid) {
  const e = pending.get(rid);
  if (!e || e.bodyTaken) return json(res, 404, { error: '没有这个请求' });
  e.bodyTaken = true;
  res.writeHead(200, { 'content-type': 'application/octet-stream', 'content-length': e.length });
  e.req.on('data', c => stats.up += c.length);
  e.req.pipe(res);
  e.req.on('error', () => res.destroy());
  res.on('close', () => { if (!res.writableFinished) e.req.destroy(); });
}

// 电脑送回完整 HTTP 回复（状态行 + 头 + 正文）
const HOP = new Set(['connection', 'keep-alive', 'transfer-encoding', 'upgrade', 'proxy-connection', 'te', 'trailer']);
function reply(req, res, rid) {
  const e = pending.get(rid);
  if (!e || e.replied) { req.resume(); return json(res, 404, { error: '没有这个请求' }); }
  e.replied = true; clearTimeout(e.timer);
  const out = e.res;
  let head = Buffer.alloc(0), started = false;
  const end = ok => { pending.delete(rid); if (!e.bodyTaken) e.req.resume(); json(res, ok ? 200 : 502, { ok }); };
  req.on('data', chunk => {
    if (started) {
      stats.down += chunk.length;
      if (!out.write(chunk)) { req.pause(); out.once('drain', () => req.resume()); }
      return;
    }
    head = Buffer.concat([head, chunk]);
    const i = head.indexOf('\r\n\r\n');
    if (i < 0) { if (head.length > 32768) { req.destroy(); json(out, 502, { error: '回复格式不对' }); } return; }
    const lines = head.subarray(0, i).toString('utf8').split('\r\n');
    const m = /^HTTP\/1\.[01] (\d{3})/.exec(lines[0]);
    if (!m) { req.destroy(); json(out, 502, { error: '回复格式不对' }); return; }
    const h = {};
    for (const l of lines.slice(1)) { const c = l.indexOf(':'); if (c > 0) { const k = l.slice(0, c).trim().toLowerCase(); if (!HOP.has(k)) h[k] = l.slice(c + 1).trim(); } }
    if (!out.destroyed) out.writeHead(+m[1], h);
    started = true;
    const rest = head.subarray(i + 4);
    if (rest.length) { stats.down += rest.length; out.write(rest); }
  });
  req.on('end', () => { if (!started) { json(out, 502, { error: '回复不完整' }); return end(false); } out.end(); end(true); });
  req.on('error', () => { pending.delete(rid); try { out.destroy(); } catch { } });
  out.on('close', () => { if (!out.writableFinished) req.destroy(); });
}

// ───── 路由 ─────
const server = http.createServer((req, res) => {
  let url; try { url = new URL(req.url, 'http://x'); } catch { return json(res, 400, { error: '地址不对' }); }
  const p = url.pathname, parts = p.split('/');
  try {
    if (p === '/' || p === '/health') return json(res, 200, { ok: true, app: 'LanBridge relay', online: [...devices.values()].filter(online).length, pending: pending.size, requests: stats.requests, upMB: +(stats.up / 1048576).toFixed(1), downMB: +(stats.down / 1048576).toFixed(1), uptime: Math.round((Date.now() - stats.started) / 1000) });
    if (parts[1] === 'p' && req.method === 'GET') return poll(req, res, parts[2] || '', url.searchParams);
    if (parts[1] === 'b' && req.method === 'GET') return body(req, res, parts[2] || '');
    if (parts[1] === 'r' && req.method === 'POST') return reply(req, res, parts[2] || '');
    if (['t', 'm', 'c'].includes(parts[1]) && parts[2]) return tunnel(req, res, parts[1], parts[2], parts.length > 3 ? '/' + parts.slice(3).join('/') : '', url.search);
    json(res, 404, { error: '没有这个地址' });
  } catch (err) { log('出错', err); json(res, 500, { error: '中转出错' }); }
});
server.requestTimeout = 0;      // 大文件上传不能被总时长掐断
server.headersTimeout = 30000;
server.keepAliveTimeout = 65000;
server.listen(PORT, HOST, () => log(`轻桥中转在 http://${HOST}:${PORT}，单次上传上限 ${MAX_BODY / 1048576} MB`));

setInterval(() => {
  const now = Date.now();
  for (const [c, v] of codes) if (v.until < now) { codes.delete(c); const d = devices.get(v.id); if (d && d.code === c) d.code = null; }
  for (const [id, d] of devices) if (!d.waiters.length && now - (d.last || d.seen) > 90 * 86400e3) { devices.delete(id); dirty = true; }
  save();
}, 30000).unref();
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => { save(); process.exit(0); });
