using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LanBridge;

static class Crypto {
 static readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();
 public static byte[] Random(int n) { var b = new byte[n]; lock (rng) rng.GetBytes(b); return b; }
 public static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
 public static string Token(int bytes) => Hex(Random(bytes));
 public static byte[] Hmac(byte[] key, byte[] data) { using var h = new HMACSHA256(key); return h.ComputeHash(data); }
 public static byte[] Hmac(byte[] key, string s) => Hmac(key, Encoding.UTF8.GetBytes(s));
 public static string Sha(byte[] b) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(b)); }
 public static string Sha(string s) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))); }
 public static bool Same(byte[] a, byte[] b) {
  if (a == null || b == null || a.Length != b.Length) return false;
  int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0;
 }
}

/// <summary>
/// 一组设备共用的密钥。由它派生出：请求签名、内容加密（AES-256-CBC）、完整性校验（HMAC-SHA256，先加密后校验）。
/// 局域网里电脑之间传的文字、图片、文件都经过它加密。
/// </summary>
sealed class Keys {
 public readonly byte[] Enc, Mac, Sig; public readonly string Tag;
 public Keys(byte[] key) {
  Enc = Crypto.Hmac(key, "lanbridge/2/enc"); Mac = Crypto.Hmac(key, "lanbridge/2/mac"); Sig = Crypto.Hmac(key, "lanbridge/2/sig");
  Tag = Crypto.Hex(Crypto.Hmac(key, "lanbridge/2/tag")).Substring(0, 12);
 }
 public static long SealedLength(long n) => 16 + (n / 16 + 1) * 16 + 32;

 public byte[] Seal(byte[] plain) {
  using var aes = Aes.Create(); aes.Key = Enc; aes.GenerateIV();
  byte[] ct; using (var e = aes.CreateEncryptor()) ct = e.TransformFinalBlock(plain, 0, plain.Length);
  var o = new byte[16 + ct.Length + 32];
  Buffer.BlockCopy(aes.IV, 0, o, 0, 16); Buffer.BlockCopy(ct, 0, o, 16, ct.Length);
  using (var h = new HMACSHA256(Mac)) Buffer.BlockCopy(h.ComputeHash(o, 0, 16 + ct.Length), 0, o, 16 + ct.Length, 32);
  return o;
 }

 public byte[] Open(byte[] s) {
  if (s == null || s.Length < 64 || (s.Length - 48) % 16 != 0) throw new ApiError(400, "数据格式不对");
  byte[] tag; using (var h = new HMACSHA256(Mac)) tag = h.ComputeHash(s, 0, s.Length - 32);
  var got = new byte[32]; Buffer.BlockCopy(s, s.Length - 32, got, 0, 32);
  if (!Crypto.Same(tag, got)) throw new ApiError(401, "校验失败：两台设备的密钥不一致");
  using var aes = Aes.Create(); aes.Key = Enc; var iv = new byte[16]; Buffer.BlockCopy(s, 0, iv, 0, 16); aes.IV = iv;
  using var d = aes.CreateDecryptor(); return d.TransformFinalBlock(s, 16, s.Length - 48);
 }

 /// <summary>把 length 字节明文加密后写到 output。总长度正好是 SealedLength(length)。</summary>
 public void SealStream(Stream input, long length, Stream output, Action<long> progress) {
  using var aes = Aes.Create(); aes.Key = Enc; aes.GenerateIV();
  using var mac = new HMACSHA256(Mac);
  output.Write(aes.IV, 0, 16); mac.TransformBlock(aes.IV, 0, 16, null, 0);
  var tee = new MacTee(output, mac);
  using (var cs = new CryptoStream(tee, aes.CreateEncryptor(), CryptoStreamMode.Write)) {
   var buf = new byte[128 * 1024]; long done = 0;
   while (done < length) {
    int n = input.Read(buf, 0, (int)Math.Min(buf.Length, length - done));
    if (n <= 0) throw new IOException("文件读取中断（文件可能被改动了）");
    cs.Write(buf, 0, n); done += n; progress?.Invoke(done);
   }
   cs.FlushFinalBlock();
  }
  mac.TransformFinalBlock(new byte[0], 0, 0);
  output.Write(mac.Hash, 0, 32);
 }

 /// <summary>读 sealedLength 字节密文，解密写到 output，最后校验；校验不过抛异常（调用方删掉半成品）。</summary>
 public void OpenStream(Stream input, long sealedLength, Stream output, Action<long> progress) {
  if (sealedLength < 64 || (sealedLength - 48) % 16 != 0) throw new ApiError(400, "数据长度不对");
  using var aes = Aes.Create(); aes.Key = Enc;
  var iv = new byte[16]; Util.ReadExact(input, iv, 0, 16); aes.IV = iv;
  using var mac = new HMACSHA256(Mac); mac.TransformBlock(iv, 0, 16, null, 0);
  long ct = sealedLength - 48, done = 0;
  var sink = new NoClose(output);
  using (var cs = new CryptoStream(sink, aes.CreateDecryptor(), CryptoStreamMode.Write)) {
   var buf = new byte[128 * 1024];
   while (done < ct) {
    int n = input.Read(buf, 0, (int)Math.Min(buf.Length, ct - done));
    if (n <= 0) throw new IOException("传输中断");
    mac.TransformBlock(buf, 0, n, null, 0); cs.Write(buf, 0, n); done += n; progress?.Invoke(done);
   }
   try { cs.FlushFinalBlock(); } catch (CryptographicException) { throw new ApiError(400, "数据损坏"); }
  }
  mac.TransformFinalBlock(new byte[0], 0, 0);
  var tag = new byte[32]; Util.ReadExact(input, tag, 0, 32);
  if (!Crypto.Same(mac.Hash, tag)) throw new ApiError(400, "校验失败，文件在传输中被改动或损坏");
 }

 sealed class MacTee : Stream {
  readonly Stream inner; readonly HMACSHA256 mac;
  public MacTee(Stream inner, HMACSHA256 mac) { this.inner = inner; this.mac = mac; }
  public override void Write(byte[] b, int o, int n) { if (n == 0) return; mac.TransformBlock(b, o, n, null, 0); inner.Write(b, o, n); }
  public override void Flush() => inner.Flush();
  protected override void Dispose(bool disposing) { }
  public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
  public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
  public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
  public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
 }
}

sealed class NoClose : Stream {
 readonly Stream inner; public NoClose(Stream s) { inner = s; }
 public override void Write(byte[] b, int o, int n) => inner.Write(b, o, n);
 public override int Read(byte[] b, int o, int n) => inner.Read(b, o, n);
 public override void Flush() => inner.Flush();
 protected override void Dispose(bool disposing) { try { inner.Flush(); } catch { } }
 public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => inner.CanWrite;
 public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
 public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
}

/// <summary>首次配对时的 ECDH（P-256）。两边算出同一把临时密钥，用它把设备组密钥安全交给新电脑；
/// 再由它算出 6 位核对码，两台屏幕上显示的一样就说明中间没人掉包。</summary>
sealed class Handshake : IDisposable {
 readonly ECDiffieHellmanCng ecdh = new ECDiffieHellmanCng(256) { KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash, HashAlgorithm = CngAlgorithm.Sha256 };
 public string Public => Convert.ToBase64String(ecdh.PublicKey.ToByteArray());
 public byte[] Derive(string otherPublic) {
  byte[] blob; try { blob = Convert.FromBase64String(otherPublic ?? ""); } catch { throw new ApiError(400, "配对数据无效"); }
  if (blob.Length != 72) throw new ApiError(400, "配对数据无效");
  using var k = ECDiffieHellmanCngPublicKey.FromByteArray(blob, CngKeyBlobFormat.EccPublicBlob);
  return ecdh.DeriveKeyMaterial(k);
 }
 public static string Code(byte[] shared) => (BitConverter.ToUInt32(Crypto.Hmac(shared, "lanbridge/2/sas"), 0) % 1000000).ToString("D6");
 public void Dispose() => ecdh.Dispose();
}
