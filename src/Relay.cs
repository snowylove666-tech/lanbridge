using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace LanBridge;

/// <summary>
/// 跨网络：一直挂着一个到中转服务的长轮询，别的网络里的电脑和手机经中转找到这台电脑。
/// 中转把请求原样交过来，这里交给同一个 Router 处理，再把完整回复送回去。
/// 电脑之间的内容本来就用设备组密钥加密，中转看不到明文。
/// </summary>
sealed class Relay {
 /// <summary>内置的常驻中转：阿里云北京那台 Windows 服务器（带 1 GB 小云）。用户在界面上点“开启跨网络”并同意后才用；也可以填自己的。组内自动同步。</summary>
 public const string Default = "http://47.95.194.127:49330";
 const long CodeLife = 10 * 60 * 1000;
 const string CodeChars = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

 readonly App app; Config C => app.C;
 Action<Req, Res> handler;
 readonly AutoResetEvent signal = new AutoResetEvent(false);
 volatile bool running = true; volatile HttpWebRequest current;
 public volatile string State = "off", Error; public long LastOk, MaxBody = 300L << 20;
 /// <summary>中转带的小云文件池大小（MB）；0 表示没有（临时中转、Node 版中转）。</summary>
 public long PoolMB;
 public string Code; public long CodeUntil;

 public Relay(App app) { this.app = app; }

 /// <summary>当前用的中转地址；null 表示没开。</summary>
 /// <summary>内置中转（测试时可以用 --builtin-relay 换成本机的）。</summary>
 public string Builtin => app.Opt.RelayDefault ?? Default;

 public string Base {
  get {
   string u = (C.relay ?? "").Trim().TrimEnd('/');
   return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? u : null;
  }
 }
 public bool Ready => Base != null && State == "ok";
 public string Origin { get { var b = Base; return b == null ? null : new Uri(b).GetLeftPart(UriPartial.Authority); } }
 public string PeerUrl(string id) { var b = Base; return b == null ? null : b + "/t/" + id; }
 public string PhoneUrl { get { var b = Base; return b == null ? null : b + "/m/" + C.deviceId + "/"; } }
 public bool IsMine(string url) { var b = Base; return b != null && url != null && url.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase); }

 public void Start(Action<Req, Res> h) { handler = h; Util.Thread("relay", Loop); }
 public void Stop() { running = false; Kick(); }
 /// <summary>地址或配对码变了：断开当前轮询，马上重新连。</summary>
 public void Kick() { try { current?.Abort(); } catch { } signal.Set(); }

 /// <summary>临时中转开在这台电脑上时：发往隧道地址的请求直接走本地，不绕 Cloudflare。</summary>
 public static volatile string LocalFrom, LocalTo;

 public static HttpWebRequest Make(string url, string method, int timeout, bool viaPublic = false) {
  string from = LocalFrom, to = LocalTo;
  bool local = !viaPublic && from != null && to != null && url.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase);
  var r = Client.Make(local ? to + url.Substring(from.Length) : url, method, timeout);
  // 中转在公网上：跟浏览器一样走系统代理（开着 Clash 之类时由它决定直连还是代理）。
  if (!local) r.Proxy = WebRequest.DefaultWebProxy;
  return r;
 }

 /// <summary>
 /// 给别处电脑输入的连接码。用内置中转时就是 8 位码；用临时中转或自己的中转时后面带上中转地址，
 /// 对方不用先设置中转，输入就能连：K7M2-PQ9X@lands-rated-tune-plot（临时隧道只写名字）、K7M2-PQ9X@relay.example.com。
 /// </summary>
 public string CodeText {
  get {
   var b = Base; string c; lock (app.Gate) c = Code != null && CodeUntil > Util.Ms() ? Code : null;
   if (c == null || b == null) return null;
   string pretty = c.Substring(0, 4) + "-" + c.Substring(4);
   if (b == Builtin) return pretty;
   return pretty + "@" + ShortHost(b);
  }
 }
 public static string ShortHost(string url) {
  var u = new Uri(url); string h = u.Host.ToLowerInvariant();
  if (h.EndsWith(".trycloudflare.com")) return h.Substring(0, h.Length - ".trycloudflare.com".Length);
  string hp = u.IsDefaultPort ? h : h + ":" + u.Port;
  return u.Scheme == "http" && !IsIpOrLocal(h) ? "http://" + hp : hp;
 }
 static bool IsIpOrLocal(string h) => h == "localhost" || IPAddress.TryParse(h, out _);
 /// <summary>连接码 @ 后面的部分还原成中转地址。</summary>
 public static string FromShortHost(string h) {
  h = (h ?? "").Trim().TrimEnd('/');
  if (h.Length == 0 || h.Length > 200) return null;
  if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || h.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
   return Uri.TryCreate(h, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null;
  h = h.ToLowerInvariant();
  if (!h.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '.' || ch == ':')) return null;
  if (!h.Contains('.') && !h.Contains(':')) return "https://" + h + ".trycloudflare.com";
  string host = h.Split(':')[0];
  return (IsIpOrLocal(host) ? "http://" : "https://") + h;
 }

 /// <summary>“不在同一个网络”配对用的 8 位码，10 分钟有效。</summary>
 public string NewCode() {
  if (Base == null) throw new ApiError(400, "还没有设置中转服务器（设置 → 跨网络）");
  lock (app.Gate) {
   if (Code == null || CodeUntil - Util.Ms() < 3 * 60 * 1000) {
    var b = Crypto.Random(8); var s = new char[8];
    for (int i = 0; i < 8; i++) s[i] = CodeChars[b[i] % CodeChars.Length];
    Code = new string(s);
   }
   CodeUntil = Util.Ms() + CodeLife;
  }
  Kick(); app.Changes.Bump();
  return Code;
 }
 public bool CodeActive { get { lock (app.Gate) return Code != null && CodeUntil > Util.Ms(); } }

 void Loop() {
  int fails = 0, group = 0;
  while (running) {
   string b = Base;
   if (b == null) { SetState("off", null); signal.WaitOne(5000); continue; }
   if (State == "off") SetState("connecting", null);
   string url = b + "/p/" + C.deviceId + "?k=" + C.relayKey + (CodeActive ? "&code=" + Code : "") + (State != "ok" ? "&hi=1" : "");
   bool aborted = false;
   try {
    var r = Make(url, "GET", State == "ok" ? 32000 : 10000); current = r;
    // 睡眠唤醒、换网络后，系统留着的旧连接有时已经坏了（报“无法访问已释放的对象”）还一直被拿来用：连不上就换一组新连接。
    if (group > 0) r.ConnectionGroupName = "poll" + group;
    // 手机热点、流量这类网络会悄悄掐掉半分钟没动静的连接：开 TCP 保活（10 秒一次），连接不会被当成闲置，断了也能很快发现。
    try { r.ServicePoint.SetTcpKeepAlive(true, 10000, 3000); } catch { }
    var d = J.Obj(Client.Send(r, null));
    current = null; if (fails >= 2) Log.Write("中转恢复了"); fails = 0; LastOk = Util.Ms();
    long max = d.Num("max"); if (max > 0) MaxBody = max;
    long pool = d.Num("pool"); if (pool != PoolMB) { PoolMB = pool; app.Changes.Bump(); }
    SetState("ok", null);
    if (d.TryGetValue("reqs", out var raw) && raw is IEnumerable list)
     foreach (var x in list) if (x is Dictionary<string, object> it) { var item = it; Util.Thread("relay-req", () => Serve(item)); }
   } catch (Exception e) {
    current = null;
    aborted = e is WebException we && we.Status == WebExceptionStatus.RequestCanceled;
    if (!running) return;
    if (aborted) continue;
    fails++;
    // 断一次先马上重连（中转那边 10 秒内还当这台在线，手机不会感觉到）；连着两次不行才算连不上。
    if (fails == 1 && !(e is ApiError)) { Log.Write("中转断了一下，马上重连：" + e.Message.Split('\n')[0].Trim()); continue; }
    group++;
    SetState("error", e is ApiError ? e.Message : "连不上中转服务器");
    if (fails == 2 || fails % 20 == 0) Log.Write("中转连不上：" + e.Message.Split('\n')[0].Trim());
    signal.WaitOne(Math.Min(2000 * (fails - 1), 30000));
   }
  }
 }

 void SetState(string s, string err) {
  bool changed = State != s || Error != err;
  State = s; Error = err;
  if (changed) {
   app.Changes.Bump();
   // 中转刚连上：让各条连接马上试一次（不在同一个网络的电脑可以经中转连上了）。
   if (s == "ok") foreach (var l in app.Hub.Links()) if (!l.PushOk) l.Signal.Set();
  }
 }

 static readonly HashSet<string> PhonePaths = new HashSet<string> { "/v1/swap", "/v1/sync", "/v1/ack", "/", "/apple-touch-icon.png", "/favicon.ico" };
 bool Allowed(string kind, string method, string path) {
  switch (kind) {
   case "t": return path.StartsWith("/lb/") && !path.StartsWith("/lb/join");
   case "c": return CodeActive && (path == "/lb/join" || path == "/lb/join/poll" || path == "/lb/hello");
   case "m":
    if (path.StartsWith("/api/phone/") || path.StartsWith("/v1/")) return true;
    return (method == "GET" || method == "HEAD") && (PhonePaths.Contains(path) || path.StartsWith("/assets/"));
  }
  return false;
 }

 /// <summary>处理一个经中转来的请求。</summary>
 void Serve(Dictionary<string, object> it) {
  string rid = it.Get("rid"), b = Base;
  if (rid.Length == 0 || b == null) return;
  var q = new Req { Method = it.Get("method").ToUpperInvariant(), Target = it.Get("target"), Version = "HTTP/1.1", Remote = IPAddress.None, Via = it.Get("kind"), Length = it.Num("length") };
  if (it.TryGetValue("headers", out var hr) && hr is Dictionary<string, object> hs) foreach (var kv in hs) q.Headers[kv.Key] = Convert.ToString(kv.Value);
  int qi = q.Target.IndexOf('?');
  try { q.Path = Uri.UnescapeDataString(qi < 0 ? q.Target : q.Target.Substring(0, qi)); } catch { q.Path = "/"; }
  q.QueryString = qi < 0 ? "" : q.Target.Substring(qi + 1);
  q.Host = new Uri(b).Authority;
  using var body = new BodyIn(b + "/b/" + rid, q.Length); q.Body = body;
  using var outStream = new ReplyOut(b + "/r/" + rid);
  var res = new Res(outStream);
  try {
   if (!q.Target.StartsWith("/") || q.Target.StartsWith("//") || !Allowed(q.Via, q.Method, q.Path)) throw new ApiError(404, "没有这个地址");
   handler(q, res);
   if (!res.Sent) res.Json(new { error = "没有内容" }, 404);
  } catch (ApiError e) { if (!res.Sent) Try(() => res.Json(new { error = e.Message }, e.Status)); }
  catch (Exception e) when (e is IOException || e is WebException || e is ObjectDisposedException) { outStream.Broken = true; }
  catch (Exception e) { Log.Write("经中转的请求出错 " + q.Path + "：" + e); if (!res.Sent) Try(() => res.Json(new { error = "出错了：" + e.Message }, 500)); }
 }
 static void Try(Action a) { try { a(); } catch { } }

 /// <summary>请求体：第一次读的时候才去中转取。</summary>
 sealed class BodyIn : Stream {
  readonly string url; long left; HttpWebResponse resp; Stream s;
  public BodyIn(string url, long length) { this.url = url; left = length; }
  public override int Read(byte[] buf, int o, int n) {
   if (left <= 0) return 0;
   if (s == null) {
    var r = Make(url, "GET", 60000);
    resp = (HttpWebResponse)r.GetResponse(); s = resp.GetResponseStream();
   }
   int got = s.Read(buf, o, (int)Math.Min(n, left));
   if (got <= 0) throw new IOException("请求体中断了");
   left -= got; return got;
  }
  protected override void Dispose(bool d) { try { s?.Dispose(); resp?.Dispose(); } catch { } base.Dispose(d); }
  public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
  public override long Length => left; public override long Position { get => 0; set => throw new NotSupportedException(); }
  public override void Flush() { }
  public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
  public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
 }

 /// <summary>回复：第一次写的时候才打开到中转的上传（长轮询要等一会儿才有回复）。</summary>
 sealed class ReplyOut : Stream {
  readonly string url; HttpWebRequest r; Stream s; public bool Broken;
  public ReplyOut(string url) { this.url = url; }
  public override void Write(byte[] b, int o, int n) {
   if (s == null) {
    r = Make(url, "POST", 120000);
    r.SendChunked = true; r.AllowWriteStreamBuffering = false; r.ContentType = "application/octet-stream";
    s = r.GetRequestStream();
   }
   s.Write(b, o, n);
  }
  public override void Flush() { s?.Flush(); }
  protected override void Dispose(bool d) {
   if (s != null) {
    try { s.Dispose(); if (!Broken) using (var resp = (HttpWebResponse)r.GetResponse()) { } } catch { }
    s = null;
   }
   base.Dispose(d);
  }
  public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
  public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
  public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
  public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
 }
}
