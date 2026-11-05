# 轻桥中转服务

两台设备不在同一个网络时（家里 ↔ 公司，手机用流量），轻桥经这里转一下。一个文件 `relay.mjs`，零依赖，Node 18 以上。

- 只在内存里转发，不存内容。电脑之间的内容在电脑上已经加密，中转看不到；手机经中转的内容中转能看到，所以放在自己的服务器上。
- 只记每台电脑的设备 ID 和轮询密钥哈希（`STATE` 文件），防止别人冒充它收请求。
- 资源：空闲约 40 MB 内存，CPU 几乎为零。流量就是经中转传的内容：剪贴板文字很小，截图几百 KB，发文件按文件大小算（进一次、出一次）。

## 两种中转

| | 常驻中转 | 临时中转 |
|---|---|---|
| 在哪 | 一台一直开着的服务器（我们的，或客户自己的） | 组里的一台电脑，经 Cloudflare 免费临时隧道连到外网 |
| 怎么开 | 轻桥里点“开启跨网络 → 常驻中转”；自己的服务器用下面一行命令装 | 轻桥里点“开启跨网络 → 临时中转”，不用装任何东西 |
| 地址 | 固定不变 | 每次重启都变，别处的电脑要再输入一次连接码 |
| 程序 | `relay.mjs`（Node） | 电脑里内置的 `src/RelayServer.cs`（同一套协议，`RELAY_IMPL=pc node tests/relay.mjs` 测它） |

## Windows 服务器（我们的阿里云用这个）：常驻中转 + 小云

`LanBridge.exe --relay-server` 只当中转：不开窗口、不碰剪贴板，约 30 MB 内存，还带**小云**文件池（默认 1 GB，满了从最早放上来的开始删）。管理员 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File install-windows.ps1 -Url <LanBridge.exe 的下载地址> -Sha256 <哈希>
```

装到 `C:\ProgramData\LanBridgeRelay`，计划任务 `LanBridgeRelay`（开机运行、LOCAL SERVICE、低优先级、崩了自动重启），Windows 防火墙放行端口。云服务器的安全组要另外放行 TCP 49330。选项：`-Port`、`-PoolMB`（0 关掉小云）、`-MaxMB`、`-Uninstall`。

小云：文件在电脑上用每个文件自己的随机密钥加密后才上传（文件名也在密文里），密钥再用设备组密钥加密存在服务器上，所以服务器只有密文。组里的电脑都能列出和下载；“复制链接”是 `/d/文件ID#密钥`，`#` 后面浏览器不会发给服务器，下载页在浏览器里解密（http 页面没有 WebCrypto 时用页面里自带的纯 JS AES/SHA-256，`node tests/poolcrypto.mjs` 和 Node 对过）。Node 版 `relay.mjs` 暂时不带小云。

## 一行命令装常驻中转（任何 Linux 服务器）

```bash
# 有域名（先把域名解析到服务器）：自动装 Caddy、申请 HTTPS 证书
curl -fsSL https://raw.githubusercontent.com/snowylove666-tech/lanbridge/main/relay/install.sh | sudo bash -s -- --domain relay.example.com
# 没有域名：http://服务器IP:49330
curl -fsSL https://raw.githubusercontent.com/snowylove666-tech/lanbridge/main/relay/install.sh | sudo bash
```

脚本做的事：有 Node 18+ 就用，没有就下载官方 Node 放进 `/opt/lanbridge-relay/node`（校验 SHA256，不动系统）；写 systemd 服务（独立用户、限 128 MB 内存、开机自启）；有域名时 80/443 空着就装 Caddy 自动 HTTPS，已经有网页服务器占着就只打印一段反向代理配置。最后显示中转地址，填到轻桥 **设置 → 跨网络 → 用自己的服务器当常驻中转**，组里其他电脑会自动跟着改。

选项：`--port`、`--bind`、`--max-mb`（前面是 Cloudflare 免费版代理时用 95）、`--memory`、`--no-caddy`、`--uninstall`。

`install.sh` 是 `node relay/make-install.mjs` 用 `install.tpl.sh` + `relay.mjs` 生成的（`build.ps1` 会自动跑），不要直接改它。

## 手动部署（Ubuntu + Caddy）

```bash
sudo mkdir -p /opt/lanbridge-relay && sudo cp relay.mjs /opt/lanbridge-relay/
sudo cp lanbridge-relay.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now lanbridge-relay
curl -s http://127.0.0.1:49330/health
```

Caddy 加一段：

```
lanbridge.example.com {
	reverse_proxy 127.0.0.1:49330 {
		flush_interval -1
	}
}
```

内置的常驻中转地址写在 `src/Relay.cs` 的 `Default`，打包后新装的电脑点一下就能用。

没有 Node 时用 Docker：

```bash
docker run -d --name lanbridge-relay --restart unless-stopped -p 127.0.0.1:49330:49330 \
  -e HOST=0.0.0.0 -e STATE=/data/relay-state.json -v /opt/lanbridge-relay:/app -v lanbridge-relay:/data -w /app \
  --memory 128m node:22-alpine node relay.mjs
```

## 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `PORT` | 49330 | 监听端口 |
| `HOST` | 127.0.0.1 | 只给本机的 Caddy 用；直接对外时改成 0.0.0.0（不建议，没有 HTTPS） |
| `MAX_BODY_MB` | 300 | 单次上传上限；电脑会自动得知，超过时直接提示 |
| `STATE` | ./relay-state.json | 设备密钥哈希 |

`GET /health` 返回在线电脑数、累计请求数和流量。
