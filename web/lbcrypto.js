// 轻桥 · 手机网页直接读写小云用的加密（纯 JS：中转是 http，浏览器不给 WebCrypto）。
// 格式和电脑一样：Keys(key) 派生 enc = HMAC(key,"lanbridge/2/enc")、mac = HMAC(key,"lanbridge/2/mac")；
// 密文 = IV(16) + AES-256-CBC(PKCS7) + HMAC-SHA256(IV+密文)。
'use strict';
const LBC = (() => {
  const K256 = new Uint32Array([0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2]);
  function Sha256() { this.h = new Uint32Array([0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19]); this.buf = new Uint8Array(64); this.n = 0; this.len = 0; this.w = new Uint32Array(64); }
  Sha256.prototype.block = function (b, o) {
    const w = this.w, h = this.h;
    for (let i = 0; i < 16; i++) w[i] = b[o + 4 * i] << 24 | b[o + 4 * i + 1] << 16 | b[o + 4 * i + 2] << 8 | b[o + 4 * i + 3];
    for (let i = 16; i < 64; i++) { const x = w[i - 15], y = w[i - 2]; w[i] = (((x >>> 7 | x << 25) ^ (x >>> 18 | x << 14) ^ x >>> 3) + w[i - 7] + ((y >>> 17 | y << 15) ^ (y >>> 19 | y << 13) ^ y >>> 10) + w[i - 16]) | 0; }
    let a = h[0], b2 = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
    for (let i = 0; i < 64; i++) {
      const t1 = (hh + ((e >>> 6 | e << 26) ^ (e >>> 11 | e << 21) ^ (e >>> 25 | e << 7)) + (e & f ^ ~e & g) + K256[i] + w[i]) | 0;
      const t2 = (((a >>> 2 | a << 30) ^ (a >>> 13 | a << 19) ^ (a >>> 22 | a << 10)) + (a & b2 ^ a & c ^ b2 & c)) | 0;
      hh = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b2; b2 = a; a = (t1 + t2) | 0;
    }
    h[0] += a; h[1] += b2; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
  };
  Sha256.prototype.update = function (d) {
    let i = 0; this.len += d.length;
    if (this.n) { while (this.n < 64 && i < d.length) this.buf[this.n++] = d[i++]; if (this.n === 64) { this.block(this.buf, 0); this.n = 0; } }
    for (; i + 64 <= d.length; i += 64) this.block(d, i);
    while (i < d.length) this.buf[this.n++] = d[i++];
    return this;
  };
  Sha256.prototype.digest = function () {
    const bits = this.len * 8, pad = new Uint8Array((this.n < 56 ? 56 : 120) - this.n + 8); pad[0] = 0x80;
    const hi = Math.floor(bits / 4294967296), lo = bits >>> 0, L = pad.length;
    pad[L - 8] = hi >>> 24; pad[L - 7] = hi >>> 16; pad[L - 6] = hi >>> 8; pad[L - 5] = hi; pad[L - 4] = lo >>> 24; pad[L - 3] = lo >>> 16; pad[L - 2] = lo >>> 8; pad[L - 1] = lo;
    this.update(pad); const out = new Uint8Array(32);
    for (let i = 0; i < 8; i++) { out[4 * i] = this.h[i] >>> 24; out[4 * i + 1] = this.h[i] >>> 16; out[4 * i + 2] = this.h[i] >>> 8; out[4 * i + 3] = this.h[i]; }
    return out;
  };
  function hmacInit(key) { if (key.length > 64) key = new Sha256().update(key).digest(); const k = new Uint8Array(64); k.set(key); const ip = k.map(x => x ^ 0x36), op = k.map(x => x ^ 0x5c); const s = new Sha256().update(ip); return { update(d) { s.update(d); return this; }, digest() { return new Sha256().update(op).update(s.digest()).digest(); } }; }
  const te = new TextEncoder(), td = new TextDecoder();
  const hmac = (key, msg) => hmacInit(key).update(typeof msg === 'string' ? te.encode(msg) : msg).digest();

  // AES 表
  const SB = new Uint8Array(256), SI = new Uint8Array(256), TE = [0, 1, 2, 3].map(() => new Uint32Array(256)), TD = [0, 1, 2, 3].map(() => new Uint32Array(256));
  (() => {
    const d = new Uint8Array(256), th = new Uint8Array(256);
    for (let i = 0; i < 256; i++) th[(d[i] = i << 1 ^ (i >> 7) * 283) ^ i] = i;
    for (let x = 0, xi = 0; !SB[x]; x ^= d[x] || 1, xi = th[xi] || 1) {
      let s = xi ^ xi << 1 ^ xi << 2 ^ xi << 3 ^ xi << 4; s = s >> 8 ^ s & 255 ^ 99;
      SB[x] = s; SI[s] = x;
      const x2 = d[x], x4 = d[x2], x8 = d[x4];
      let t = x8 * 0x1010101 ^ x4 * 0x10001 ^ x2 * 0x101 ^ x * 0x1010100;
      for (let i = 0; i < 4; i++) TD[i][s] = t = t << 24 ^ t >>> 8;
      t = d[s] * 0x101 ^ s * 0x1010100;
      for (let i = 0; i < 4; i++) TE[i][x] = t = t << 24 ^ t >>> 8;
    }
  })();
  function encKey(key) {
    const n = 8, ek = []; let rcon = 1, i;
    for (i = 0; i < n; i++) ek[i] = key[4 * i] << 24 | key[4 * i + 1] << 16 | key[4 * i + 2] << 8 | key[4 * i + 3];
    for (; i < 60; i++) {
      let t = ek[i - 1];
      if (i % n === 0 || i % n === 4) {
        t = SB[t >>> 24] << 24 ^ SB[t >> 16 & 255] << 16 ^ SB[t >> 8 & 255] << 8 ^ SB[t & 255];
        if (i % n === 0) { t = t << 8 ^ t >>> 24 ^ rcon << 24; rcon = rcon << 1 ^ (rcon >> 7) * 283; }
      }
      ek[i] = ek[i - n] ^ t;
    }
    return ek;
  }
  function decKey(key) {
    const ek = encKey(key), dk = []; let i = 60;
    for (let j = 0; i; j++, i--) {
      const t = ek[j & 3 ? i : i - 4];
      dk[j] = i <= 4 || j < 4 ? t : TD[0][SB[t >>> 24]] ^ TD[1][SB[t >> 16 & 255]] ^ TD[2][SB[t >> 8 & 255]] ^ TD[3][SB[t & 255]];
    }
    return new Int32Array(dk);
  }
  // 一块：in 是 4 个字（已与前一块/IV 异或好），返回 4 个字
  function crypt(k, x0, x1, x2, x3, dec) {
    const [t0, t1, t2, t3] = dec ? TD : TE, sb = dec ? SI : SB;
    let a = x0 ^ k[0], b = (dec ? x3 : x1) ^ k[1], c = x2 ^ k[2], d = (dec ? x1 : x3) ^ k[3], ki = 4;
    for (let r = 0; r < 13; r++) {
      const a2 = t0[a >>> 24] ^ t1[b >> 16 & 255] ^ t2[c >> 8 & 255] ^ t3[d & 255] ^ k[ki];
      const b2 = t0[b >>> 24] ^ t1[c >> 16 & 255] ^ t2[d >> 8 & 255] ^ t3[a & 255] ^ k[ki + 1];
      const c2 = t0[c >>> 24] ^ t1[d >> 16 & 255] ^ t2[a >> 8 & 255] ^ t3[b & 255] ^ k[ki + 2];
      d = t0[d >>> 24] ^ t1[a >> 16 & 255] ^ t2[b >> 8 & 255] ^ t3[c & 255] ^ k[ki + 3];
      ki += 4; a = a2; b = b2; c = c2;
    }
    const w = [0, 0, 0, 0];
    for (let i = 0; i < 4; i++) { w[dec ? 3 & -i : i] = sb[a >>> 24] << 24 ^ sb[b >> 16 & 255] << 16 ^ sb[c >> 8 & 255] << 8 ^ sb[d & 255] ^ k[ki++]; const x = a; a = b; b = c; c = d; d = x; }
    return w;
  }
  const rd = (p, o) => p[o] << 24 | p[o + 1] << 16 | p[o + 2] << 8 | p[o + 3];
  const wr = (p, o, v) => { p[o] = v >>> 24; p[o + 1] = v >> 16; p[o + 2] = v >> 8; p[o + 3] = v; };

  function derive(key) { return { enc: hmac(key, 'lanbridge/2/enc'), mac: hmac(key, 'lanbridge/2/mac') }; }
  function random(n) { const b = new Uint8Array(n); for (let i = 0; i < n; i += 65536) crypto.getRandomValues(b.subarray(i, Math.min(n, i + 65536))); return b; }

  /** 加密：和电脑的 Keys.Seal 一样。 */
  function seal(key, plain) {
    const { enc, mac } = derive(key), k = encKey(enc), iv = random(16);
    const pad = 16 - plain.length % 16, n = plain.length + pad, out = new Uint8Array(16 + n + 32);
    out.set(iv, 0);
    const p = new Uint8Array(n); p.set(plain); p.fill(pad, plain.length);
    let v0 = rd(iv, 0), v1 = rd(iv, 4), v2 = rd(iv, 8), v3 = rd(iv, 12);
    for (let o = 0; o < n; o += 16) {
      const w = crypt(k, rd(p, o) ^ v0, rd(p, o + 4) ^ v1, rd(p, o + 8) ^ v2, rd(p, o + 12) ^ v3, false);
      v0 = w[0]; v1 = w[1]; v2 = w[2]; v3 = w[3];
      wr(out, 16 + o, v0); wr(out, 20 + o, v1); wr(out, 24 + o, v2); wr(out, 28 + o, v3);
    }
    out.set(hmacInit(mac).update(out.subarray(0, 16 + n)).digest(), 16 + n);
    return out;
  }
  /** 解密：校验不过抛错。 */
  function open(key, data) {
    if (data.length < 64 || (data.length - 48) % 16) throw new Error('数据格式不对');
    const { enc, mac } = derive(key);
    const tag = hmacInit(mac).update(data.subarray(0, data.length - 32)).digest();
    let diff = 0; for (let i = 0; i < 32; i++) diff |= tag[i] ^ data[data.length - 32 + i];
    if (diff) throw new Error('校验失败');
    const k = decKey(enc), ct = data.subarray(16, data.length - 32), out = new Uint8Array(ct.length);
    let p0 = rd(data, 0), p1 = rd(data, 4), p2 = rd(data, 8), p3 = rd(data, 12);
    for (let o = 0; o < ct.length; o += 16) {
      const c0 = rd(ct, o), c1 = rd(ct, o + 4), c2 = rd(ct, o + 8), c3 = rd(ct, o + 12);
      const w = crypt(k, c0, c1, c2, c3, true);
      wr(out, o, w[0] ^ p0); wr(out, o + 4, w[1] ^ p1); wr(out, o + 8, w[2] ^ p2); wr(out, o + 12, w[3] ^ p3);
      p0 = c0; p1 = c1; p2 = c2; p3 = c3;
    }
    const pad = out[out.length - 1];
    if (pad < 1 || pad > 16) throw new Error('数据格式不对');
    return out.subarray(0, out.length - pad);
  }
  const b64 = b => { let s = ''; for (let i = 0; i < b.length; i += 0x8000) s += String.fromCharCode.apply(null, b.subarray(i, i + 0x8000)); return btoa(s); };
  const unb64 = s => { s = s.replace(/-/g, '+').replace(/_/g, '/'); s += '='.repeat((4 - s.length % 4) % 4); return Uint8Array.from(atob(s), c => c.charCodeAt(0)); };
  const b64url = b => b64(b).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
  return { seal, open, random, b64, unb64, b64url, hmac, te, td };
})();
if (typeof module !== 'undefined') module.exports = LBC;
