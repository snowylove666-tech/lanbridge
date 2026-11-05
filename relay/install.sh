#!/usr/bin/env bash
# 轻桥常驻中转：在任何一台 Linux 服务器上一行命令装好（我们自己的服务器也用这一份）。
#
#   有域名（推荐，自动 HTTPS）：
#     curl -fsSL https://raw.githubusercontent.com/snowylove666-tech/lanbridge/main/relay/install.sh | sudo bash -s -- --domain relay.example.com
#   没有域名（http://服务器IP:49330）：
#     curl -fsSL https://raw.githubusercontent.com/snowylove666-tech/lanbridge/main/relay/install.sh | sudo bash
#
# 选项：
#   --domain 域名     用 Caddy 自动申请 HTTPS 证书（域名要先解析到这台服务器，80/443 端口要空着；
#                     已经有网页服务器占着 80/443 时，脚本只装中转，并打印一段反向代理配置给你加上）
#   --port 端口       中转自己听的端口，默认 49330
#   --bind 地址       中转听的地址；有域名时默认 127.0.0.1（只让 Caddy 访问），没有域名时默认 0.0.0.0
#   --max-mb 数字     单次上传上限，默认 300（前面是 Cloudflare 免费版代理时用 95）
#   --memory 数字     内存上限（MB），默认 128
#   --no-caddy        有域名也不装 Caddy（你自己配反向代理）
#   --uninstall       卸载
#
# 装好后在轻桥里：设置 → 跨网络 → 用自己的服务器当常驻中转，填脚本最后显示的地址。
set -euo pipefail

DOMAIN=""; PORT=49330; BIND=""; MAXMB=300; MEM=128; CADDY=1; ACTION=install
while [ $# -gt 0 ]; do
  case "$1" in
    --domain) DOMAIN="${2:-}"; shift 2 ;;
    --port) PORT="${2:-}"; shift 2 ;;
    --bind) BIND="${2:-}"; shift 2 ;;
    --max-mb) MAXMB="${2:-}"; shift 2 ;;
    --memory) MEM="${2:-}"; shift 2 ;;
    --no-caddy) CADDY=0; shift ;;
    --uninstall) ACTION=uninstall; shift ;;
    -h|--help) sed -n '2,22p' "$0" 2>/dev/null || true; exit 0 ;;
    *) echo "不认识的选项：$1（--help 看用法）"; exit 1 ;;
  esac
done

say() { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m出错：\033[0m%s\n' "$*" >&2; exit 1; }

[ "$(id -u)" = 0 ] || die "要用 root 运行（命令前面加 sudo）"
command -v systemctl >/dev/null || die "这台服务器没有 systemd，请看 relay/README.md 用 Docker 装"
case "$PORT$MAXMB$MEM" in *[!0-9]*) die "--port / --max-mb / --memory 要填数字" ;; esac
if [ -n "$DOMAIN" ] && ! printf '%s' "$DOMAIN" | grep -Eq '^[A-Za-z0-9.-]+\.[A-Za-z]{2,}$'; then die "域名不对：$DOMAIN"; fi
[ -n "$BIND" ] || { [ -n "$DOMAIN" ] && BIND=127.0.0.1 || BIND=0.0.0.0; }

DIR=/opt/lanbridge-relay
UNIT=/etc/systemd/system/lanbridge-relay.service
MARK="# 轻桥中转（lanbridge-relay）"

if [ "$ACTION" = uninstall ]; then
  say "卸载轻桥中转"
  systemctl disable --now lanbridge-relay 2>/dev/null || true
  rm -f "$UNIT"; systemctl daemon-reload
  rm -rf "$DIR" /var/lib/lanbridge-relay /var/lib/private/lanbridge-relay
  if [ -f /etc/caddy/Caddyfile ] && grep -q "$MARK" /etc/caddy/Caddyfile; then
    awk -v m="$MARK" '$0==m{skip=1} skip&&/^# 轻桥中转结束/{skip=0;next} !skip' /etc/caddy/Caddyfile > /etc/caddy/Caddyfile.lbtmp && cat /etc/caddy/Caddyfile.lbtmp > /etc/caddy/Caddyfile && rm -f /etc/caddy/Caddyfile.lbtmp
    systemctl reload caddy 2>/dev/null || true
  fi
  say "已卸载"; exit 0
fi

fetch() { if command -v curl >/dev/null; then curl -fsSL "$1" -o "$2"; else wget -qO "$2" "$1"; fi; }
mkdir -p "$DIR"

# ───── Node.js：系统里有 18 以上就用，没有就下载官方版本放到 /opt/lanbridge-relay/node（不动系统里的东西） ─────
NODE=""
if command -v node >/dev/null && [ "$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0)" -ge 18 ]; then
  NODE="$(command -v node)"
elif [ -x "$DIR/node/bin/node" ]; then
  NODE="$DIR/node/bin/node"
else
  case "$(uname -m)" in x86_64|amd64) ARCH=x64 ;; aarch64|arm64) ARCH=arm64 ;; armv7l) ARCH=armv7l ;; *) die "不支持的 CPU：$(uname -m)" ;; esac
  say "下载 Node.js（linux-$ARCH，约 25 MB）"
  BASE=https://nodejs.org/dist/latest-v20.x
  TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
  fetch "$BASE/SHASUMS256.txt" "$TMP/SUMS"
  FILE="$(grep -o "node-v[0-9.]*-linux-$ARCH\.tar\.xz" "$TMP/SUMS" | head -1)"
  [ -n "$FILE" ] || die "找不到 Node.js 下载文件"
  fetch "$BASE/$FILE" "$TMP/$FILE"
  (cd "$TMP" && grep " $FILE\$" SUMS | sha256sum -c - >/dev/null) || die "Node.js 下载文件校验不对"
  command -v xz >/dev/null || { command -v apt-get >/dev/null && apt-get install -y -qq xz-utils >/dev/null; } || true
  rm -rf "$DIR/node"; mkdir -p "$DIR/node"
  tar -xJf "$TMP/$FILE" -C "$DIR/node" --strip-components=1
  NODE="$DIR/node/bin/node"
fi
say "Node.js $("$NODE" -v)：$NODE"

# ───── 中转程序本体（和 relay/relay.mjs 一样） ─────
cat > "$DIR/relay.mjs" <<'LANBRIDGE_RELAY_EOF'
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
LANBRIDGE_RELAY_EOF
chmod 644 "$DIR/relay.mjs"

# ───── 开机自启（systemd，独立用户、限内存） ─────
cat > "$UNIT" <<EOF
[Unit]
Description=LanBridge relay (轻桥中转)
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=$NODE $DIR/relay.mjs
Environment=PORT=$PORT HOST=$BIND MAX_BODY_MB=$MAXMB STATE=/var/lib/lanbridge-relay/relay-state.json
DynamicUser=yes
StateDirectory=lanbridge-relay
Restart=always
RestartSec=3
MemoryMax=${MEM}M
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable lanbridge-relay >/dev/null 2>&1
systemctl restart lanbridge-relay
for i in $(seq 1 30); do
  if (command -v curl >/dev/null && curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1) || (command -v wget >/dev/null && wget -qO- "http://127.0.0.1:$PORT/health" >/dev/null 2>&1); then OK=1; break; fi
  sleep 0.5
done
[ "${OK:-0}" = 1 ] || { journalctl -u lanbridge-relay -n 20 --no-pager || true; die "中转没起来（上面是日志）"; }
say "中转已运行：127.0.0.1:$PORT（单次上限 $MAXMB MB，内存上限 $MEM MB）"

SNIPPET="$MARK
$DOMAIN {
	reverse_proxy 127.0.0.1:$PORT {
		flush_interval -1
	}
}
# 轻桥中转结束"

port_busy() { (command -v ss >/dev/null && ss -ltn "( sport = :$1 )" | grep -q LISTEN) || false; }

URL=""
if [ -n "$DOMAIN" ] && [ "$CADDY" = 1 ]; then
  if command -v caddy >/dev/null && systemctl list-unit-files caddy.service >/dev/null 2>&1 && [ -f /etc/caddy/Caddyfile ]; then
    if grep -q "$MARK" /etc/caddy/Caddyfile; then say "Caddy 里已经有轻桥中转的配置"
    else printf '\n%s\n' "$SNIPPET" >> /etc/caddy/Caddyfile; say "已加到 /etc/caddy/Caddyfile"; fi
    systemctl reload caddy || systemctl restart caddy
    URL="https://$DOMAIN"
  elif port_busy 80 || port_busy 443; then
    echo
    echo "80/443 端口已经被别的网页服务器占着，中转已装好，但 HTTPS 要你加一段反向代理："
    echo "  Caddy：把下面这段加到 Caddyfile"
    printf '%s\n' "$SNIPPET" | sed 's/^/    /'
    echo "  Nginx：location / { proxy_pass http://127.0.0.1:$PORT; proxy_buffering off; proxy_request_buffering off; client_max_body_size ${MAXMB}m; proxy_read_timeout 120s; }"
    URL="https://$DOMAIN（加好反向代理后）"
  elif command -v apt-get >/dev/null; then
    say "安装 Caddy（自动申请 HTTPS 证书）"
    apt-get update -qq && apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https curl gnupg >/dev/null
    curl -fsSL https://dl.cloudsmith.io/public/caddy/stable/gpg.key | gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
    curl -fsSL https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt > /etc/apt/sources.list.d/caddy-stable.list
    apt-get update -qq && apt-get install -y -qq caddy >/dev/null
    printf '%s\n' "$SNIPPET" > /etc/caddy/Caddyfile
    systemctl enable caddy >/dev/null 2>&1; systemctl restart caddy
    URL="https://$DOMAIN"
  elif command -v dnf >/dev/null || command -v yum >/dev/null; then
    say "安装 Caddy（自动申请 HTTPS 证书）"
    PM="$(command -v dnf || command -v yum)"
    "$PM" install -y -q 'dnf-command(copr)' >/dev/null 2>&1 || true
    "$PM" copr enable -y @caddy/caddy >/dev/null 2>&1 || true
    "$PM" install -y -q caddy >/dev/null || die "Caddy 装不上，可以加 --no-caddy 自己配反向代理"
    printf '%s\n' "$SNIPPET" > /etc/caddy/Caddyfile
    systemctl enable caddy >/dev/null 2>&1; systemctl restart caddy
    URL="https://$DOMAIN"
  else
    echo "没认出这台服务器的软件包管理器，Caddy 没装。请自己配反向代理到 127.0.0.1:$PORT。"
  fi
elif [ -n "$DOMAIN" ]; then
  URL="https://$DOMAIN（你自己配好反向代理后）"
fi

if [ -z "$URL" ]; then
  IP="$( (curl -fsS -4 --max-time 5 https://api.ipify.org || hostname -I | awk '{print $1}') 2>/dev/null )"
  URL="http://${IP:-服务器IP}:$PORT"
  if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then ufw allow "$PORT/tcp" >/dev/null && say "已在 ufw 放行 $PORT 端口"; fi
  echo "提示：云服务器（阿里云、腾讯云、谷歌云等）还要在网页控制台的安全组/防火墙里放行 TCP $PORT。"
fi

echo
say "装好了。在轻桥里：设置 → 跨网络 → 用自己的服务器当常驻中转，填："
echo
echo "    $URL"
echo
echo "看状态：systemctl status lanbridge-relay    看日志：journalctl -u lanbridge-relay -f    卸载：加 --uninstall 再运行一次"
