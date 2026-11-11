using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanBridge;

/// <summary>
/// 电脑上的中转服务，和 relay/relay.mjs 是同一套协议（测试对两边都跑一遍）。
/// 临时中转用它：这台电脑开着它，再经 Cloudflare 临时隧道连到外网，别处的电脑和手机经隧道找到组里的电脑。
/// 只在内存里转发，不存内容。只听 127.0.0.1，外面的请求都经隧道程序进来。
/// </summary>
sealed class RelayServer : IDisposable {
 const long StartMs = 60000, PollMs = 25000, OnlineMs = 10000, CodeMs = 120000;
 const string CodeChars = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
 readonly HttpServer http; readonly object gate = new object();
 public readonly long MaxBody; public int Port => http.Port;
 readonly Dictionary<string, Dev> devices = new Dictionary<string, Dev>();
 readonly Dictionary<string, (string id, long until)> codes = new Dictionary<string, (string, long)>();
 readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
 readonly long started = Util.Ms(); long requests, up, down;
 /// <summary>小文件池（只有服务器上的常驻中转开着）。</summary>
 readonly FilePool pool;
 readonly Timer sweep;

 sealed class Dev { public string KeyHash, Code; public long Seen; public readonly List<Waiter> Waiters = new List<Waiter>(); public readonly List<Dictionary<string, object>> Queue = new List<Dictionary<string, object>>(); }
 sealed class Waiter { public readonly ManualResetEventSlim Ev = new ManualResetEventSlim(false); public Dictionary<string, object> Item; public TcpClient Conn; }
 sealed class Pending {
  public Req Req; public Res Res; public Stream Body; public long Length; public bool BodyTaken, Replied;
  public readonly ManualResetEventSlim Started = new ManualResetEventSlim(false), Finished = new ManualResetEventSlim(false);
 }

 public RelayServer(int port, long maxBody, IPAddress bind = null, FilePool pool = null) {
  MaxBody = maxBody; this.pool = pool; bind ??= IPAddress.Loopback;
  http = new HttpServer(bind, port, Handle, chunked: true, maxConnections: 400, publicOk: !IPAddress.IsLoopback(bind));
  http.Start();
  sweep = new Timer(_ => Sweep(), null, 30000, 30000);
 }
 public void Dispose() {
  try { sweep.Dispose(); } catch { }
  http.Dispose();
  lock (gate) { foreach (var d in devices.Values) foreach (var w in d.Waiters) w.Ev.Set(); foreach (var p in pending.Values) { p.Started.Set(); p.Finished.Set(); } }
 }

 static bool IsId(string s) => s.Length == 16 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
 static bool IsCode(string s) => s.Length == 8 && s.All(c => CodeChars.IndexOf(c) >= 0);
 bool Online(Dev d) => d != null && (d.Waiters.Count > 0 || Util.Ms() - d.Seen < OnlineMs);
 static bool Alive(TcpClient c) { try { return c != null && c.Connected && !(c.Client.Poll(0, SelectMode.SelectRead) && c.Client.Available == 0); } catch { return false; } }

 void Handle(Req q, Res res) {
  var parts = q.Path.Split('/');
  string a = parts.Length > 1 ? parts[1] : "", b = parts.Length > 2 ? parts[2] : "";
  if (q.Path == "/" || q.Path == "/health") {
   int online; lock (gate) online = devices.Values.Count(Online);
   res.Json(new { ok = true, app = "LanBridge relay", host = "pc", ver = Program.Version, online, pending = pending.Count, requests, upMB = Math.Round(up / 1048576.0, 1), downMB = Math.Round(down / 1048576.0, 1), uptime = (Util.Ms() - started) / 1000, pool = pool?.Info });
   return;
  }
  if (pool != null && pool.Handle(q, res, parts)) return;
  if (a == "p" && q.Method == "GET") { Poll(q, res, b); return; }
  if (a == "b" && q.Method == "GET") { Body(res, b); return; }
  if (a == "r" && q.Method == "POST") { Reply(q, res, b); return; }
  if ((a == "t" || a == "m" || a == "c") && b.Length > 0) { Tunnel(q, res, a, b, parts.Length > 3 ? "/" + string.Join("/", parts.Skip(3)) : ""); return; }
  res.Json(new { error = "没有这个地址" }, 404);
 }

 // ───── 电脑来取请求 ─────
 void Poll(Req q, Res res, string id) {
  string k = q.Q("k");
  if (!IsId(id) || k.Length < 16 || k.Length > 128) { res.Json(new { error = "参数不对" }, 400); return; }
  string kh = Crypto.Sha(Encoding.UTF8.GetBytes(k)); long now = Util.Ms();
  Waiter w = null; List<Dictionary<string, object>> items = null; Dev d;
  lock (gate) {
   if (!devices.TryGetValue(id, out d)) devices[id] = d = new Dev();
   if (d.KeyHash != null && d.KeyHash != kh) { res.Json(new { error = "这个设备 ID 已被另一台电脑使用" }, 403); return; }
   d.KeyHash = kh; d.Seen = now;
   string code = q.Q("code").ToUpperInvariant();
   if (IsCode(code)) {
    if (!codes.TryGetValue(code, out var c) || c.id == id || c.until < now) {
     if (d.Code != null && d.Code != code) codes.Remove(d.Code);
     d.Code = code; codes[code] = (id, now + CodeMs);
    }
   } else if (d.Code != null) { codes.Remove(d.Code); d.Code = null; }
   // hi=1：电脑刚连上来，马上回一声，它就知道中转是通的。
   if (d.Queue.Count > 0 || q.Q("hi").Length > 0) { items = d.Queue.Take(32).ToList(); d.Queue.RemoveRange(0, items.Count); }
   else { w = new Waiter { Conn = q.Conn }; d.Waiters.Add(w); }
  }
  if (w != null) {
   w.Ev.Wait((int)PollMs);
   lock (gate) { d.Waiters.Remove(w); d.Seen = Util.Ms(); items = w.Item != null ? new List<Dictionary<string, object>> { w.Item } : new List<Dictionary<string, object>>(); w.Item = null; }
  }
  try { res.Json(new { reqs = items, max = MaxBody, pool = pool == null ? 0 : pool.Cap >> 20 }); }
  catch { lock (gate) d.Queue.InsertRange(0, items); throw; }
 }

 bool Deliver(Dev d, Dictionary<string, object> item) {
  while (d.Waiters.Count > 0) {
   var w = d.Waiters[0]; d.Waiters.RemoveAt(0);
   if (!Alive(w.Conn)) { w.Ev.Set(); continue; }
   w.Item = item; w.Ev.Set(); return true;
  }
  if (d.Queue.Count >= 64) return false;
  d.Queue.Add(item); return true;
 }

 // ───── 别处找电脑 ─────
 static readonly HashSet<string> Pass = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "content-type", "content-length", "authorization", "x-lb-token", "x-lb-id", "x-lb-ts", "x-lb-nonce", "x-lb-sig", "x-lb-port", "range", "user-agent", "origin", "sec-fetch-site" };
 static readonly HashSet<string> PhonePaths = new HashSet<string> { "/v1/swap", "/v1/sync", "/v1/ack" };
 static bool Allowed(string kind, string method, string path) {
  switch (kind) {
   case "t": return path.StartsWith("/lb/") && !path.StartsWith("/lb/join");
   case "c": return path == "/lb/join" || path == "/lb/join/poll" || path == "/lb/hello";
   case "m": return path.StartsWith("/api/phone/") || PhonePaths.Contains(path)
     || ((method == "GET" || method == "HEAD") && (path == "/" || path.StartsWith("/assets/") || path == "/apple-touch-icon.png" || path == "/favicon.ico"));
  }
  return false;
 }

 const string OfflinePage = "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>轻桥</title>"
  + "<style>body{margin:0;font:16px/1.6 -apple-system,BlinkMacSystemFont,'PingFang SC','Microsoft YaHei',sans-serif;background:#f6f5f2;color:#1d1d1f;display:grid;place-items:center;min-height:100vh}"
  + "@media(prefers-color-scheme:dark){body{background:#1c1c1e;color:#f2f1ee}.c{background:#262628!important}}"
  + ".c{margin:20px;max-width:420px;padding:28px 22px;border-radius:18px;background:#fff;text-align:center;box-shadow:0 2px 14px rgba(0,0,0,.06)}h1{font-size:20px;margin:0 0 10px}p{margin:8px 0;color:#8b877f;font-size:14.5px}"
  + "ul{text-align:left;font-size:14.5px;padding-left:22px;margin:12px 0}button{margin-top:14px;width:100%;height:46px;border:0;border-radius:12px;background:#3d6bf5;color:#fff;font-size:16px}</style></head>"
  + "<body><div class=\"c\"><h1>电脑现在不在线</h1><p>手机这边没问题，是那台电脑连不上：</p><ul><li>电脑关机或睡眠了</li><li>电脑上的轻桥没在运行</li><li>电脑暂时断网了</li></ul>"
  + "<p id=\"s\">电脑一回来，这一页会自己打开轻桥。</p><button onclick=\"location.reload()\">再试一次</button></div>"
  + "<script>setInterval(function(){fetch(location.href,{cache:'no-store'}).then(function(r){if(r.ok)location.reload()}).catch(function(){})},5000)</script></body></html>";

 void Tunnel(Req q, Res res, string kind, string key, string rest) {
  string id = key;
  if (kind == "c") {
   string code = new string(key.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
   lock (gate) { if (!codes.TryGetValue(code, out var c) || c.until < Util.Ms()) { res.Json(new { error = "配对码不对，或那台电脑已经关了轻桥" }, 404); return; } id = c.id; }
  } else if (!IsId(id)) { res.Json(new { error = "没有这台设备" }, 404); return; }
  string search = q.QueryString.Length > 0 ? "?" + q.QueryString : "";
  if (kind == "m" && rest == "") { res.Bytes(new byte[0], "text/plain", 301, new Dictionary<string, string> { ["Location"] = "/m/" + id + "/" + search }); return; }
  if (!Allowed(kind, q.Method, rest)) { res.Json(new { error = "不允许经中转访问这个地址" }, 403); return; }
  Dev d; lock (gate) devices.TryGetValue(id, out d);
  bool online; lock (gate) online = Online(d);
  if (!online) {
   // 手机直接打开网页时给一张看得懂的页面（以前是一行 JSON），电脑回来了自己刷新。
   if (kind == "m" && q.Method == "GET" && rest == "/") { res.Bytes(Encoding.UTF8.GetBytes(OfflinePage), "text/html; charset=utf-8", 503, new Dictionary<string, string> { ["Cache-Control"] = "no-store" }); return; }
   res.Json(new { error = "那台电脑现在不在线（没开机，或没运行轻桥）" }, 503); return;
  }
  Stream body = q.Body; long length = q.Length;
  if (q.Chunked) {
   // 经隧道来的请求有时是分块的：先收下来（有上限），电脑那边要知道长度。
   var m = new MemoryStream(); var buf = new byte[65536]; int n;
   while ((n = q.Body.Read(buf, 0, buf.Length)) > 0) { m.Write(buf, 0, n); if (m.Length > MaxBody) { res.Close = true; res.Json(new { error = "经中转一次最多传 " + (MaxBody >> 20) + " MB，大文件请在同一个网络里传" }, 413); return; } }
   m.Position = 0; body = m; length = m.Length;
  }
  if (length > MaxBody) { res.Close = true; res.Json(new { error = "经中转一次最多传 " + (MaxBody >> 20) + " MB，大文件请在同一个网络里传" }, 413); return; }
  var headers = new Dictionary<string, object>();
  foreach (var kv in q.Headers) if (Pass.Contains(kv.Key)) headers[kv.Key.ToLowerInvariant()] = kv.Value;
  if (q.Chunked) headers["content-length"] = length.ToString();
  string rid = Crypto.Token(12);
  var p = new Pending { Req = q, Res = res, Body = body, Length = length, BodyTaken = length == 0 };
  var item = new Dictionary<string, object> { ["rid"] = rid, ["kind"] = kind, ["method"] = q.Method, ["target"] = rest + search, ["headers"] = headers, ["length"] = length };
  lock (gate) {
   pending[rid] = p;
   if (!Deliver(d, item)) { pending.Remove(rid); res.Json(new { error = "那台电脑太忙了，请稍后再试" }, 503); return; }
   requests++;
  }
  if (!p.Started.Wait((int)StartMs)) {
   bool timedOut;
   lock (gate) { timedOut = !p.Replied; if (timedOut) { pending.Remove(rid); p.Replied = true; } }
   if (timedOut) { res.Close = true; res.Json(new { error = "那台电脑没有响应" }, 504); return; }
  }
  p.Finished.Wait();
  lock (gate) pending.Remove(rid);
  if (!res.Sent) { res.Close = true; res.Json(new { error = "回复不完整" }, 502); }
 }

 // 电脑取请求体
 void Body(Res res, string rid) {
  Pending p;
  lock (gate) { if (!pending.TryGetValue(rid, out p) || p.BodyTaken) { res.Json(new { error = "没有这个请求" }, 404); return; } p.BodyTaken = true; }
  var o = res.Begin(200, "application/octet-stream", p.Length);
  var buf = new byte[65536]; long left = p.Length; int n;
  try {
   while (left > 0 && (n = p.Body.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0) { o.Write(buf, 0, n); left -= n; Interlocked.Add(ref up, n); }
   o.Flush();
  } catch { res.Close = true; throw; }
  if (left > 0) { res.Close = true; throw new IOException("请求体不完整"); }
 }

 // 电脑送回完整 HTTP 回复（状态行 + 头 + 正文）
 static readonly HashSet<string> Hop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "connection", "keep-alive", "transfer-encoding", "upgrade", "proxy-connection", "te", "trailer" };
 void Reply(Req q, Res res, string rid) {
  Pending p;
  lock (gate) { if (!pending.TryGetValue(rid, out p) || p.Replied) { res.Json(new { error = "没有这个请求" }, 404); return; } p.Replied = true; }
  p.Started.Set();
  bool ok = false;
  try {
   var head = new MemoryStream(); int match = 0, c;
   while (match < 4) {
    c = q.Body.ReadByte(); if (c == -1) break;
    head.WriteByte((byte)c); if (head.Length > 32768) break;
    match = c == (match % 2 == 0 ? 13 : 10) ? match + 1 : c == 13 ? 1 : 0;
   }
   var lines = match == 4 ? Encoding.UTF8.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None) : new string[0];
   var first = lines.Length > 0 ? lines[0].Split(' ') : new string[0];
   if (first.Length < 2 || !first[0].StartsWith("HTTP/1.") || !int.TryParse(first[1], out int status)) { p.Res.Close = true; p.Res.Json(new { error = "回复格式不对" }, 502); }
   else {
    var sb = new StringBuilder("HTTP/1.1 " + status + " " + Res.Reason(status) + "\r\n"); bool hasLength = false;
    for (int i = 1; i < lines.Length && lines[i].Length > 0; i++) {
     int k = lines[i].IndexOf(':'); if (k < 1) continue;
     string name = lines[i].Substring(0, k).Trim();
     if (Hop.Contains(name)) continue;
     if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) hasLength = true;
     sb.Append(name).Append(": ").Append(lines[i].Substring(k + 1).Trim()).Append("\r\n");
    }
    if (!hasLength) p.Res.Close = true;
    if (p.Res.Close) sb.Append("Connection: close\r\n");
    sb.Append("\r\n");
    var o = p.Res.Raw(); var hb = Encoding.UTF8.GetBytes(sb.ToString()); o.Write(hb, 0, hb.Length);
    var buf = new byte[65536]; int n;
    while ((n = q.Body.Read(buf, 0, buf.Length)) > 0) { o.Write(buf, 0, n); Interlocked.Add(ref down, n); }
    o.Flush(); ok = true;
   }
  } catch { p.Res.Close = true; if (!p.Res.Sent) try { p.Res.Json(new { error = "回复不完整" }, 502); } catch { } }
  finally { p.Finished.Set(); }
  res.Json(new { ok }, ok ? 200 : 502);
 }

 void Sweep() {
  long now = Util.Ms();
  lock (gate) {
   foreach (var c in codes.Where(x => x.Value.until < now).Select(x => x.Key).ToList()) { var id = codes[c].id; codes.Remove(c); if (devices.TryGetValue(id, out var d) && d.Code == c) d.Code = null; }
   foreach (var id in devices.Where(x => x.Value.Waiters.Count == 0 && now - x.Value.Seen > 86400e3).Select(x => x.Key).ToList()) devices.Remove(id);
  }
 }
}
