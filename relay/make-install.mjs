// 生成 relay/install.sh：把 relay.mjs 原样放进安装脚本，服务器上只要下载这一个文件。
// 改了 relay.mjs 或 install.tpl.sh 后运行：node relay/make-install.mjs（build.ps1 会自动跑）
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const tpl = fs.readFileSync(path.join(here, 'install.tpl.sh'), 'utf8').replace(/\r\n/g, '\n');
const relay = fs.readFileSync(path.join(here, 'relay.mjs'), 'utf8').replace(/\r\n/g, '\n').replace(/\n+$/, '');
if (relay.includes('LANBRIDGE_RELAY_EOF')) throw new Error('relay.mjs 里不能出现 LANBRIDGE_RELAY_EOF');
const out = tpl.replace('__RELAY_MJS__', () => relay);
const dest = path.join(here, 'install.sh');
const old = fs.existsSync(dest) ? fs.readFileSync(dest, 'utf8') : '';
if (old !== out) { fs.writeFileSync(dest, out); console.log('已更新 relay/install.sh'); }
else console.log('relay/install.sh 已是最新');
