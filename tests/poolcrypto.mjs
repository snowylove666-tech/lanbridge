// 小云下载页里纯 JS 的 SHA-256 / HMAC / AES-256-CBC 解密，和 Node 自带的加密结果对一遍。
// 用法：node tests/poolcrypto.mjs
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const html = fs.readFileSync(path.join(here, '..', 'web', 'pool.html'), 'utf8');
const js = html.split('<script>')[1].split('</script>')[0].replace(/\nmain\(\);\s*$/, '\n');
const ctx = { TextEncoder, TextDecoder, Uint8Array, Uint32Array, Int32Array, Math, Error, atob, window: {}, document: {}, location: {} };
vm.createContext(ctx); vm.runInContext(js + '\nthis.api = { Sha256, hmac, openSealed };', ctx);
const { Sha256, hmac, openSealed } = ctx.api;
let passed = 0, failed = 0;
const ok = (c, n) => { if (c) { passed++; console.log('  ✓ ' + n); } else { failed++; console.log('  ✗ ' + n); } };
const hex = b => Buffer.from(b).toString('hex');

for (const n of [0, 1, 55, 56, 63, 64, 65, 1000, 100000]) {
  const d = crypto.randomBytes(n);
  if (hex(new Sha256().update(new Uint8Array(d)).digest()) !== crypto.createHash('sha256').update(d).digest('hex')) { ok(false, 'SHA-256 ' + n + ' 字节'); }
}
ok(true, 'SHA-256 各种长度和 Node 一致');
const k = crypto.randomBytes(32), m = crypto.randomBytes(5000);
ok(hex(hmac(new Uint8Array(k), new Uint8Array(m))) === crypto.createHmac('sha256', k).update(m).digest('hex'), 'HMAC-SHA256 和 Node 一致');

// 按轻桥的格式加密：Enc/Mac 由文件密钥派生，IV + CBC 密文 + HMAC
function seal(fileKey, plain) {
  const enc = crypto.createHmac('sha256', fileKey).update('lanbridge/2/enc').digest();
  const mac = crypto.createHmac('sha256', fileKey).update('lanbridge/2/mac').digest();
  const iv = crypto.randomBytes(16), c = crypto.createCipheriv('aes-256-cbc', enc, iv);
  const body = Buffer.concat([iv, c.update(plain), c.final()]);
  return Buffer.concat([body, crypto.createHmac('sha256', mac).update(body).digest()]);
}
for (const n of [0, 15, 16, 17, 4096, 3 * 1048576 + 7]) {
  const fk = crypto.randomBytes(32), plain = crypto.randomBytes(n);
  const out = await openSealed(new Uint8Array(fk), new Uint8Array(seal(fk, plain)), () => { });
  ok(Buffer.from(out).equals(plain), `AES-256-CBC 解密 ${n} 字节，内容一致`);
}
const fk = crypto.randomBytes(32), bad = seal(fk, Buffer.from('hello world')); bad[20] ^= 1;
let err = ''; try { await openSealed(new Uint8Array(fk), new Uint8Array(bad), () => { }); } catch (e) { err = e.message; }
ok(/校验失败/.test(err), '密文被改过：校验失败，不解密');
const t0 = Date.now(), big = crypto.randomBytes(20 * 1048576);
await openSealed(new Uint8Array(fk), new Uint8Array(seal(fk, big)), () => { });
console.log(`  · 20 MB 纯 JS 校验 + 解密用时 ${((Date.now() - t0) / 1000).toFixed(1)} 秒（电脑上）`);
console.log(`\n合计：${passed} 项通过，${failed} 项失败`);
process.exit(failed ? 1 : 0);
