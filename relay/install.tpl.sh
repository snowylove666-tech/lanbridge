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
__RELAY_MJS__
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
