using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanBridge;

/// <summary>一个请求。Body 只能读 Content-Length 那么多。</summary>
sealed class Req {
 public string Method, Target, Path, QueryString, Host, Version;
 public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
 public long Length; public Stream Body; public IPAddress Remote;
 /// <summary>请求体是分块传的（只有中转服务接受）；这时 Length 为 -1。</summary>
 public bool Chunked;
 /// <summary>这个请求所在的连接（中转服务用来判断长轮询的电脑是不是还连着）。</summary>
 public TcpClient Conn;
 /// <summary>经中转来的请求：t 电脑、m 手机、c 配对码；直连时为 null。</summary>
 public string Via;
 public bool Loopback => Via == null && IPAddress.IsLoopback(Remote);
 public string H(string name) => Headers.TryGetValue(name, out var v) ? v : "";
 public string Q(string key) {
  foreach (var part in (QueryString ?? "").Split('&')) {
   if (part.Length == 0) continue;
   var p = part.Split(new[] { '=' }, 2);
   if (Uri.UnescapeDataString(p[0].Replace('+', ' ')) == key) return p.Length == 2 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "";
  }
  return "";
 }
 public byte[] ReadAll(int max) {
  if (Length > max) throw new ApiError(413, "内容太大");
  var b = new byte[Length]; Util.ReadExact(Body, b, 0, (int)Length); return b;
 }
 public Dictionary<string, object> Json(int max = 8 * 1024 * 1024) {
  if (Length == 0) return new Dictionary<string, object>();
  try { return J.Obj(new UTF8Encoding(false, true).GetString(ReadAll(max))); }
  catch (ApiError) { throw; } catch { throw new ApiError(400, "JSON 格式不对"); }
 }
}

sealed class Res {
 readonly Stream s; public bool Sent, Close;
 public Res(Stream s) { this.s = s; }
 public static string Reason(int code) => code switch { 200 => "OK", 204 => "No Content", 206 => "Partial Content", 304 => "Not Modified", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 411 => "Length Required", 413 => "Payload Too Large", 429 => "Too Many Requests", 503 => "Service Unavailable", 507 => "Insufficient Storage", _ => code >= 500 ? "Error" : "OK" };
 public Stream Begin(int status, string type, long length, IDictionary<string, string> extra = null) {
  if (Sent) throw new InvalidOperationException("already sent");
  Sent = true;
  var sb = new StringBuilder();
  sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
  sb.Append("Content-Type: ").Append(type).Append("\r\n");
  sb.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
  sb.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n");
  if (extra != null) foreach (var kv in extra) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
  if (Close) sb.Append("Connection: close\r\n");
  sb.Append("\r\n");
  var h = Encoding.UTF8.GetBytes(sb.ToString()); s.Write(h, 0, h.Length);
  return s;
 }
 /// <summary>自己写完整的回复（中转服务原样转发）。</summary>
 public Stream Raw() { if (Sent) throw new InvalidOperationException("already sent"); Sent = true; return s; }
 public void Bytes(byte[] b, string type, int status = 200, IDictionary<string, string> extra = null) { Begin(status, type, b.Length, extra).Write(b, 0, b.Length); s.Flush(); }
 public void Json(object o, int status = 200) => Bytes(J.Bytes(o), "application/json; charset=utf-8", status);
 public void Text(string t, int status = 200) => Bytes(Encoding.UTF8.GetBytes(t), "text/plain; charset=utf-8", status);
}

/// <summary>
/// 小型 HTTP/1.1 服务器（TcpListener）。HttpListener 绑定局域网地址要管理员权限，所以自己写。
/// 支持 keep-alive，每个连接一个线程，长轮询不占线程池。
/// </summary>
sealed class HttpServer : IDisposable {
 readonly TcpListener listener; readonly Action<Req, Res> handler; volatile bool running; int active;
 readonly bool chunked, publicOk; readonly int maxConnections;
 public const int MaxConnections = 96;
 /// <param name="chunked">接受分块传的请求体（中转服务要收电脑送回的回复）。</param>
 public HttpServer(IPAddress bind, int port, Action<Req, Res> handler, bool chunked = false, int maxConnections = MaxConnections, bool publicOk = false) { listener = new TcpListener(bind, port); this.handler = handler; this.chunked = chunked; this.maxConnections = maxConnections; this.publicOk = publicOk; }
 public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
 public void Start() {
  try { listener.Server.ExclusiveAddressUse = true; } catch { }
  try { listener.Start(64); }
  catch (SocketException) { throw new ApiError(500, "端口 " + ((IPEndPoint)listener.LocalEndpoint).Port + " 已被占用。可能已经开着一个轻桥，或旧版轻桥还在运行，请先在托盘退出旧版。"); }
  running = true; Util.Thread("http-accept", Accept);
 }
 public void Dispose() { running = false; try { listener.Stop(); } catch { } }
 void Accept() {
  while (running) {
   TcpClient c;
   try { c = listener.AcceptTcpClient(); } catch { if (!running) return; Thread.Sleep(50); continue; }
   var ip = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
   if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
   if (!publicOk && !Util.IsPrivate(ip)) { try { c.Close(); } catch { } continue; }
   if (Interlocked.Increment(ref active) > maxConnections) { Interlocked.Decrement(ref active); try { c.Close(); } catch { } continue; }
   new Thread(() => { try { Serve(c, ip); } finally { Interlocked.Decrement(ref active); try { c.Close(); } catch { } } }) { IsBackground = true, Name = "http-conn" }.Start();
  }
 }

 void Serve(TcpClient client, IPAddress remote) {
  client.NoDelay = true; client.ReceiveTimeout = 60000; client.SendTimeout = 120000;
  var net = client.GetStream();
  for (int n = 0; n < 1000 && running; n++) {
   Req r;
   try { r = ReadHead(net, remote, chunked); } catch { return; }
   if (r == null) return;
   r.Conn = client;
   var res = new Res(net);
   if (r.H("Connection").Equals("close", StringComparison.OrdinalIgnoreCase) || r.Version == "HTTP/1.0") res.Close = true;
   var body = r.Chunked ? null : new Bounded(net, r.Length); var chunks = r.Chunked ? new ChunkedIn(net) : null;
   r.Body = (Stream)body ?? chunks;
   try {
    if (r.H("Expect").Equals("100-continue", StringComparison.OrdinalIgnoreCase)) { var c = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); net.Write(c, 0, c.Length); }
    handler(r, res);
    if (!res.Sent) res.Json(new { error = "没有内容" }, 404);
   } catch (ApiError e) { if (!res.Sent) TryJson(res, e.Status, e.Message); else return; }
   catch (IOException) { return; } catch (SocketException) { return; } catch (ObjectDisposedException) { return; }
   catch (Exception e) { Log.Write("请求出错 " + r.Path + "：" + e); if (!res.Sent) TryJson(res, 500, "出错了：" + e.Message); else return; }
   if (res.Close) { Linger(client, net); return; }
   // 没读完的请求体：小的读掉以便复用连接，大的直接断开。
   if (chunks != null && !chunks.Done) { Linger(client, net); return; }
   if (body != null && body.Remaining > 0) { if (body.Remaining > 4 * 1024 * 1024) { Linger(client, net); return; } try { body.Drain(); } catch { return; } }
  }
 }
 static void TryJson(Res res, int status, string msg) { try { res.Close = true; res.Json(new { error = msg }, status); } catch { } }

 /// <summary>先关发送方向，再把对方还在发的请求体读掉一点：否则 Windows 会用 TCP 重置代替我们的错误回复。</summary>
 static void Linger(TcpClient client, Stream net) {
  try {
   net.Flush(); client.Client.Shutdown(SocketShutdown.Send); client.ReceiveTimeout = 1000;
   var b = new byte[16384]; int left = 4 * 1024 * 1024, n;
   while (left > 0 && (n = net.Read(b, 0, Math.Min(b.Length, left))) > 0) left -= n;
  } catch { }
 }

 static Req ReadHead(Stream s, IPAddress remote, bool allowChunked) {
  var buf = new List<byte>(512); int matched = 0, b;
  while (true) {
   try { b = s.ReadByte(); } catch (IOException) { return null; }
   if (b == -1) return null;
   buf.Add((byte)b);
   if (buf.Count > 16384) throw new ApiError(431, "请求头过长");
   matched = (b == (matched % 2 == 0 ? 13 : 10)) ? matched + 1 : (b == 13 ? 1 : 0);
   if (matched == 4) break;
  }
  var lines = Encoding.UTF8.GetString(buf.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
  var first = lines[0].Split(' ');
  if (first.Length != 3 || !first[1].StartsWith("/") || first[1].StartsWith("//")) throw new ApiError(400, "请求无效");
  var r = new Req { Method = first[0].ToUpperInvariant(), Target = first[1], Version = first[2], Remote = remote };
  for (int i = 1; i < lines.Length && lines[i].Length > 0; i++) {
   int c = lines[i].IndexOf(':'); if (c < 1) continue;
   r.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
  }
  string te = r.H("Transfer-Encoding");
  if (te.Length > 0) {
   if (!allowChunked || te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) < 0) throw new ApiError(411, "需要 Content-Length");
   r.Chunked = true; r.Length = -1;
   r.Headers.Remove("Content-Length");
  }
  string len = r.H("Content-Length");
  if (!r.Chunked && len.Length > 0 && (!long.TryParse(len, NumberStyles.None, CultureInfo.InvariantCulture, out r.Length) || r.Length < 0)) throw new ApiError(400, "Content-Length 无效");
  int q = r.Target.IndexOf('?');
  r.Path = Uri.UnescapeDataString(q < 0 ? r.Target : r.Target.Substring(0, q));
  r.QueryString = q < 0 ? "" : r.Target.Substring(q + 1);
  r.Host = r.H("Host");
  return r;
 }

 /// <summary>分块传的请求体（Transfer-Encoding: chunked）。</summary>
 sealed class ChunkedIn : Stream {
  readonly Stream inner; long left; public bool Done;
  public ChunkedIn(Stream s) { inner = s; }
  string Line() {
   var sb = new StringBuilder(); int b;
   while ((b = inner.ReadByte()) != -1) { if (b == 10) break; if (b != 13) sb.Append((char)b); if (sb.Length > 4096) throw new IOException("分块格式不对"); }
   if (b == -1) throw new IOException("连接断了");
   return sb.ToString();
  }
  public override int Read(byte[] buf, int o, int n) {
   if (Done) return 0;
   if (left == 0) {
    string head = Line(); int semi = head.IndexOf(';'); if (semi >= 0) head = head.Substring(0, semi);
    if (!long.TryParse(head.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out left) || left < 0) throw new IOException("分块格式不对");
    if (left == 0) { while (Line().Length > 0) { } Done = true; return 0; }
   }
   int got = inner.Read(buf, o, (int)Math.Min(n, left));
   if (got <= 0) throw new IOException("连接断了");
   left -= got;
   if (left == 0) Line();
   return got;
  }
  public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
  public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
  public override void Flush() { }
  public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
  public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
 }

 /// <summary>只允许读 Content-Length 字节的流。</summary>
 sealed class Bounded : Stream {
  readonly Stream inner; public long Remaining;
  public Bounded(Stream s, long n) { inner = s; Remaining = n; }
  public override int Read(byte[] b, int o, int n) {
   if (Remaining <= 0) return 0;
   int r = inner.Read(b, o, (int)Math.Min(n, Remaining)); if (r > 0) Remaining -= r; return r;
  }
  public void Drain() { var b = new byte[16384]; while (Remaining > 0 && Read(b, 0, b.Length) > 0) { } }
  public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
  public override long Length => Remaining; public override long Position { get => 0; set => throw new NotSupportedException(); }
  public override void Flush() { }
  public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
  public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
 }
}

/// <summary>发给其他电脑的请求。一律不走系统代理（用户开着 Clash 之类时，局域网请求被代理会失败）。</summary>
static class Client {
 public static HttpWebRequest Make(string url, string method, int timeoutMs) {
  var r = (HttpWebRequest)WebRequest.Create(url);
  r.Method = method; r.Proxy = null; r.Timeout = timeoutMs; r.ReadWriteTimeout = timeoutMs; r.KeepAlive = true;
  r.AllowAutoRedirect = false; r.UserAgent = "LanBridge/" + Program.Version; r.ServicePoint.Expect100Continue = false;
  return r;
 }
 public static byte[] Send(HttpWebRequest r, byte[] body, string type = "application/octet-stream") {
  if (body != null) {
   r.ContentType = type; r.ContentLength = body.Length;
   using var s = r.GetRequestStream(); s.Write(body, 0, body.Length);
  }
  try {
   using var resp = (HttpWebResponse)r.GetResponse();
   return ReadBody(resp);
  } catch (WebException e) when (e.Response is HttpWebResponse er) {
   string msg; using (er) { var b = ReadBody(er); try { msg = J.Obj(b).Get("error"); } catch { msg = ""; } }
   throw new ApiError((int)er.StatusCode, msg.Length > 0 ? msg : "对方返回 " + (int)er.StatusCode);
  }
 }
 public static byte[] ReadBody(HttpWebResponse resp) {
  using var s = resp.GetResponseStream(); using var m = new MemoryStream();
  var b = new byte[16384]; int n; long total = 0;
  while ((n = s.Read(b, 0, b.Length)) > 0) { total += n; if (total > 32 * 1024 * 1024) throw new ApiError(413, "回复太大"); m.Write(b, 0, n); }
  return m.ToArray();
 }
 /// <summary>先用很短的超时试 TCP 连接，扫网段时不用每个地址都等很久。</summary>
 public static bool CanConnect(string host, int port, int ms) {
  try {
   using var c = new TcpClient(AddressFamily.InterNetwork);
   var t = c.ConnectAsync(host, port);
   return t.Wait(ms) && c.Connected;
  } catch { return false; }
 }
}
