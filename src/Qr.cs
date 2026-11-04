using System;
using System.Collections.Generic;
using System.Text;

namespace LanBridge;

/// <summary>二维码（沿用 1.5 的实现）：字节模式，版本 4-L / 5-L，单个纠错块，掩码 0。输出 SVG。</summary>
static class Qr {
 public static bool[,] Matrix(string text) {
  byte[] payload = Encoding.UTF8.GetBytes(text); if (payload.Length > 106) throw new ArgumentException("二维码内容过长");
  int N = payload.Length <= 78 ? 33 : 37, capacity = N == 33 ? 80 : 108, ecc = N == 33 ? 20 : 26;
  var bits = new List<bool>(); Add(bits, 4, 4); Add(bits, payload.Length, 8); foreach (byte b in payload) Add(bits, b, 8);
  Add(bits, 0, Math.Min(4, capacity * 8 - bits.Count)); while (bits.Count % 8 != 0) bits.Add(false);
  var data = new List<byte>(); for (int i = 0; i < bits.Count; i += 8) { int v = 0; for (int j = 0; j < 8; j++) v = (v << 1) | (bits[i + j] ? 1 : 0); data.Add((byte)v); }
  for (int i = 0; data.Count < capacity; i++) data.Add((byte)(i % 2 == 0 ? 0xec : 0x11));
  byte[] divisor = new byte[ecc]; divisor[ecc - 1] = 1; int root = 1;
  for (int i = 0; i < ecc; i++) { for (int j = 0; j < ecc; j++) divisor[j] = (byte)(Mul(divisor[j], root) ^ (j + 1 < ecc ? divisor[j + 1] : 0)); root = Mul(root, 2); }
  byte[] rem = new byte[ecc];
  foreach (byte b in data) { int factor = b ^ rem[0]; Array.Copy(rem, 1, rem, 0, ecc - 1); rem[ecc - 1] = 0; for (int j = 0; j < ecc; j++) rem[j] ^= (byte)Mul(divisor[j], factor); }
  data.AddRange(rem); bool[,] m = new bool[N, N], f = new bool[N, N];
  void Set(int x, int y, bool dark) { if (x >= 0 && y >= 0 && x < N && y < N) { m[x, y] = dark; f[x, y] = true; } }
  for (int i = 0; i < N; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
  foreach (var (cx, cy) in new[] { (3, 3), (N - 4, 3), (3, N - 4) }) for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++) { int d = Math.Max(Math.Abs(dx), Math.Abs(dy)); Set(cx + dx, cy + dy, d != 2 && d != 4); }
  for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) Set(N - 7 + dx, N - 7 + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
  int format = 0x77c4;
  for (int i = 0; i <= 5; i++) Set(8, i, ((format >> i) & 1) != 0);
  Set(8, 7, ((format >> 6) & 1) != 0); Set(8, 8, ((format >> 7) & 1) != 0); Set(7, 8, ((format >> 8) & 1) != 0);
  for (int i = 9; i < 15; i++) Set(14 - i, 8, ((format >> i) & 1) != 0);
  for (int i = 0; i < 8; i++) Set(N - 1 - i, 8, ((format >> i) & 1) != 0);
  for (int i = 8; i < 15; i++) Set(8, N - 15 + i, ((format >> i) & 1) != 0);
  Set(8, N - 8, true);
  int bit = 0;
  for (int right = N - 1; right >= 1; right -= 2) {
   if (right == 6) right = 5;
   for (int vertical = 0; vertical < N; vertical++) for (int j = 0; j < 2; j++) {
    int x = right - j, y = (((right + 1) & 2) == 0) ? N - 1 - vertical : vertical;
    if (!f[x, y]) { bool v = bit < data.Count * 8 && ((data[bit >> 3] >> (7 - (bit & 7))) & 1) != 0; m[x, y] = v ^ ((x + y) % 2 == 0); bit++; }
   }
  }
  return m;
 }
 public static string Svg(string text) {
  var m = Matrix(text); int n = m.GetLength(0), q = 3, size = n + q * 2;
  var sb = new StringBuilder();
  sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(size).Append(' ').Append(size).Append("\" shape-rendering=\"crispEdges\"><rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><path fill=\"#1d1d1f\" d=\"");
  for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (m[x, y]) sb.Append('M').Append(x + q).Append(' ').Append(y + q).Append("h1v1h-1z");
  sb.Append("\"/></svg>");
  return sb.ToString();
 }
 static void Add(List<bool> bits, int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
 static int Mul(int x, int y) { int z = 0; for (int i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11d); z ^= ((y >> i) & 1) * x; } return z; }
}
