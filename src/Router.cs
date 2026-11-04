using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace LanBridge;

/// <summary>
/// 所有 HTTP 入口：
///   /lb/*         电脑之间（除 hello/join 外都要签名，内容加密）
///   /api/local/*  本机界面（只认 127.0.0.1 + 启动时生成的令牌）
///   /api/phone/*  手机网页；/v1/sync /v1/ack 给快捷指令
///   /             手机网页；/desk 电脑界面；/get 下载轻桥本体
/// </summary>
sealed class Router {
 readonly App app; Config C => app.C;
 public Router(App app) { this.app = app; }

 public void Handle(Req q, Res res) {
  string p = q.Path;
  if (p.StartsWith("/lb/")) { Peer(q, res); return; }
  CheckHost(q);
  if (p.StartsWith("/api/local/")) { Local(q, res); return; }
  if (p.StartsWith("/api/test/")) { Test(q, res); return; }
  if (p.StartsWith("/api/phone/") || p == "/v1/sync" || p == "/v1/ack" || p == "/v1/swap") { PhoneApi(q, res); return; }
  if (q.Method == "GET" || q.Method == "HEAD") { Static(q, res); return; }
  throw new ApiError(404, "没有这个地址");
 }

 /// <summary>挡住 DNS 重绑定和别的网站发来的请求。</summary>
 void CheckHost(Req q) {
  string host = q.Host; int c = host.LastIndexOf(':');
  if (c > 0 && host.IndexOf(']') < c) host = host.Substring(0, c);
  host = host.Trim('[', ']').ToLowerInvariant();
  bool ok = host == "127.0.0.1" || host == "localhost" || host == app.MdnsName || (IPAddress.TryParse(host, out var ip) && Util.IsPrivate(ip));
  if (!ok) throw new ApiError(403, "地址不对");
  string origin = q.H("Origin");
  if (origin.Length > 0 && origin != "null" && !origin.Equals("http://" + q.Host, StringComparison.OrdinalIgnoreCase)) throw new ApiError(403, "不接受其他网页发来的请求");
  if (q.H("Sec-Fetch-Site") == "cross-site") throw new ApiError(403, "不接受跨站请求");
 }

 // ───────────── 电脑之间 ─────────────

 void Peer(Req q, Res res) {
  var hub = app.Hub; string p = q.Path;
  if (p == "/lb/hello" && q.Method == "GET") { res.Json(hub.PublicHello()); return; }
  if (p == "/lb/join" && q.Method == "POST") { res.Json(hub.JoinIn(q.Json(64 * 1024), q)); return; }
  if (p == "/lb/join/poll" && q.Method == "POST") { res.Json(hub.JoinPoll(q.Json(64 * 1024))); return; }
  var l = hub.Verify(q);
  if (p == "/lb/blob" && q.Method == "GET") { hub.BlobOut(l, q, res); return; }
  if (p == "/lb/blob" && q.Method == "PUT") { Sealed(res, hub.BlobIn(l, q)); return; }
  if (p == "/lb/wait" && q.Method == "GET") { Sealed(res, hub.WaitIn(l)); return; }
  if (q.Method != "POST") throw new ApiError(404, "没有这个接口");
  byte[] body = q.Length > 0 ? hub.Open(q.ReadAll(16 * 1024 * 1024)) : new byte[0];
  Dictionary<string, object> D() => body.Length == 0 ? new Dictionary<string, object>() : J.Obj(body);
  object r = p switch {
   "/lb/hello" => hub.HelloIn(l, D(), q),
   "/lb/offer" => hub.OfferIn(l, body),
   "/lb/commit" => hub.CommitIn(l, D()),
   "/lb/ack" => hub.AckIn(l, D()),
   "/lb/migrate" => hub.MigrateIn(l, D()),
   "/lb/block" => hub.BlockIn(l, D()),
   "/lb/leave" => hub.LeaveIn(l),
   _ => throw new ApiError(404, "没有这个接口")
  };
  Sealed(res, r);
 }
 void Sealed(Res res, object o) => res.Bytes(app.Hub.Seal(o), "application/octet-stream");

 // ───────────── 手机 ─────────────

 void PhoneApi(Req q, Res res) {
  var ph = app.Phones; string p = q.Path;
  if (p == "/api/phone/pair" && q.Method == "POST") { res.Json(ph.PairIn(q.Json(16 * 1024))); return; }
  if (p == "/api/phone/join" && q.Method == "POST") { res.Json(ph.JoinIn(q.Json(16 * 1024), q)); return; }
  if (p == "/api/phone/join/poll" && q.Method == "POST") { res.Json(ph.JoinPoll(q.Json(16 * 1024))); return; }
  var phone = ph.Auth(q);
  switch (p) {
   case "/api/phone/state": res.Json(ph.State(phone, long.TryParse(q.Q("rev"), out var r) ? r : 0)); return;
   case "/api/phone/text" when q.Method == "POST": res.Json(ph.TextIn(phone, q.Json())); return;
   case "/api/phone/upload" when q.Method == "POST": res.Json(ph.UploadIn(phone, q)); return;
   case "/api/phone/file": ph.Download(phone, q, res); return;
   case "/api/phone/forget" when q.Method == "POST": ph.Remove(phone.id); res.Json(new { ok = true }); return;
   case "/v1/sync" when q.Method == "POST": res.Json(ph.SyncIn(phone, ShortcutBody(q))); return;
   case "/v1/ack" when q.Method == "POST": res.Json(ph.AckIn(phone, ShortcutBody(q))); return;
   case "/v1/swap" when q.Method == "POST": ph.SwapIn(phone, q, res); return;
  }
  throw new ApiError(404, "没有这个接口");
 }

 /// <summary>快捷指令有时把正文作为 JSON 发，有时作为表单或纯文字发，都接。</summary>
 static Dictionary<string, object> ShortcutBody(Req q) {
  string type = q.H("Content-Type");
  if (type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || q.Length == 0) return q.Json();
  var raw = Encoding.UTF8.GetString(q.ReadAll(8 * 1024 * 1024));
  if (type.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
   var d = new Dictionary<string, object>();
   foreach (var part in raw.Split('&')) { var kv = part.Split(new[] { '=' }, 2); if (kv.Length == 2) d[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = Uri.UnescapeDataString(kv[1].Replace('+', ' ')); }
   return d;
  }
  return new Dictionary<string, object> { ["text"] = raw, ["mode"] = q.Q("mode") };
 }

 // ───────────── 本机界面 ─────────────

 void Local(Req q, Res res) {
  if (!q.Loopback || (q.H("X-LB-Local") != app.LocalToken && q.Q("t") != app.LocalToken)) throw new ApiError(401, "请从轻桥窗口打开");
  string p = q.Path.Substring("/api/local/".Length);
  if (q.Method == "GET") {
   switch (p) {
    case "state": {
     long since = long.TryParse(q.Q("rev"), out var r) ? r : 0;
     if (since > 0) app.Changes.Wait(since, 25000);
     res.Json(Snapshot()); return;
    }
    case "qr": {
     string ip = q.Q("ip"); if (ip.Length == 0) ip = Util.PrimaryIp();
     res.Bytes(Encoding.UTF8.GetBytes(Qr.Svg(PairUrl(ip))), "image/svg+xml"); return;
    }
    case "thumb": Thumb(q, res); return;
   }
   throw new ApiError(404, "没有这个接口");
  }
  if (p == "upload") { BrowserUpload(q, res); return; }
  var d = q.Json();
  object result = new { ok = true };
  switch (p) {
   case "settings": Settings(d); break;
   case "connect": app.Hub.Connect(d.Get("id")); break;
   case "connect-address": app.Hub.ConnectAddress(d.Get("address")); break;
   case "cancel-connect": app.Hub.CancelConnect(d.Get("id")); break;
   case "answer": app.Hub.Answer(d.Get("id"), d.Flag("allow") == true); break;
   case "remove": app.Hub.Remove(d.Get("id")); break;
   case "remove-phone": app.Phones.Remove(d.Get("id")); break;
   case "send": {
    var files = d.List("paths");
    if (d.Flag("pending") == true) { files = (app.PendingSend ?? new string[0]).ToList(); app.PendingSend = null; app.Changes.Bump(); }
    app.SendTo(d.Get("target"), files.ToArray()); break;
   }
   case "cancel-pending": app.PendingSend = null; app.Changes.Bump(); break;
   case "recopy": if (!app.Hub.Recopy(d.Get("id"))) throw new ApiError(409, "这条内容已经不在了"); break;
   case "retry": app.Hub.Retry(d.Get("id")); break;
   case "feed-remove": app.Feed.Remove(d.Get("id")); break;
   case "feed-clear": app.Feed.Clear(); break;
   case "pair-refresh": app.Phones.RefreshPair(); break;
   case "scan": app.Discovery.Rescan(); break;
   case "native": result = Native(d); break;
   case "welcomed": lock (app.Gate) C.welcomed = true; app.Save(); break;
   default: throw new ApiError(404, "没有这个接口");
  }
  res.Json(result);
 }

 void Settings(Dictionary<string, object> d) {
  bool shell = false;
  lock (app.Gate) {
   if (d.ContainsKey("name")) { var n = Util.Clip(d.Get("name").Trim(), 40); if (n.Length > 0) C.deviceName = n; }
   if (d.Flag("syncText") is bool a) C.syncText = a;
   if (d.Flag("syncFiles") is bool b) C.syncFiles = b;
   if (d.Flag("notify") is bool c) C.notify = c;
   if (d.Flag("paused") is bool e) C.paused = e;
   if (d.Flag("autostart") is bool f) { C.autostart = f; shell = true; }
   if (d.Flag("sendTo") is bool g) { C.sendTo = g; shell = true; }
   if (d.ContainsKey("autoLimitMB")) C.autoLimitMB = (int)Math.Max(1, Math.Min(4096, d.Num("autoLimitMB", 200)));
  }
  app.Save();
  if (shell && !app.Opt.NoShell) { Shell.SetAutostart(C.autostart); Shell.SetSendTo(C.sendTo); }
  // 设置变了马上告诉其他电脑（名字、是否接收）。
  foreach (var l in app.Hub.Links()) { l.LastHello = 0; l.Signal.Set(); }
  app.Changes.Bump();
 }

 /// <summary>需要 Windows 对话框或资源管理器的操作，回到界面线程执行。</summary>
 object Native(Dictionary<string, object> d) {
  string cmd = d.Get("cmd");
  switch (cmd) {
   case "pick": {
    string target = d.Get("target"); bool folder = d.Flag("folder") == true;
    var paths = app.OnUi(() => {
     if (folder) { using var f = new FolderBrowserDialog { Description = "选一个文件夹发送（会自动打包成 zip）", ShowNewFolderButton = false }; return f.ShowDialog(app.Owner) == DialogResult.OK ? new[] { f.SelectedPath } : null; }
     using var o = new OpenFileDialog { Multiselect = true, Title = "选择要发送的文件" }; return o.ShowDialog(app.Owner) == DialogResult.OK ? o.FileNames : null;
    }, null, 600000);
    if (paths != null && paths.Length > 0) app.SendTo(target, paths);
    return new { ok = true, count = paths?.Length ?? 0 };
   }
   case "receive-folder": {
    var dir = app.OnUi(() => { using var f = new FolderBrowserDialog { Description = "收到的文件存到哪里", SelectedPath = C.receiveFolder }; return f.ShowDialog(app.Owner) == DialogResult.OK ? f.SelectedPath : null; }, null, 600000);
    if (dir != null) { lock (app.Gate) C.receiveFolder = dir; app.Save(); app.Changes.Bump(); }
    return new { ok = true };
   }
   case "open-receive": Shell.OpenFolder(C.receiveFolder); break;
   case "open": case "reveal": {
    var f = app.Feed.Find(d.Get("id")); int i = (int)d.Num("i");
    var file = f?.files.ElementAtOrDefault(i);
    if (file?.path == null || !File.Exists(file.path)) throw new ApiError(404, "文件已被移动或删除");
    if (cmd == "open") Shell.Open(file.path); else Shell.Reveal(file.path);
    break;
   }
   case "firewall": return new { ok = Shell.AllowFirewall() };
   case "browser": System.Diagnostics.Process.Start(app.DeskUrl); break;
   case "exit": app.Ui(app.Exit); break;
   default: throw new ApiError(400, "不认识的操作");
  }
  return new { ok = true };
 }

 /// <summary>在普通浏览器里打开电脑界面时（没有 WebView2），拖进来的文件先上传到本机再发。</summary>
 void BrowserUpload(Req q, Res res) {
  string dir = Path.Combine(app.Root, "stage", "upload-" + Crypto.Token(4)); Directory.CreateDirectory(dir);
  string path = Path.Combine(dir, Util.SafeName(q.Q("name")));
  using (var f = File.Create(path)) q.Body.CopyTo(f);
  app.SendTo(q.Q("target"), new[] { path });
  res.Json(new { ok = true });
 }

 void Thumb(Req q, Res res) {
  var f = app.Feed.Find(q.Q("id")); int.TryParse(q.Q("i"), out int i);
  var file = f?.files.ElementAtOrDefault(i);
  if (file?.path == null || !File.Exists(file.path)) throw new ApiError(404, "没有图片");
  var fi = new FileInfo(file.path); if (fi.Length > 64 * 1024 * 1024) throw new ApiError(413, "图片太大");
  res.Bytes(File.ReadAllBytes(file.path), PhoneHub.MimeOf(file.path));
 }

 public string PairUrl(string ip) => "http://" + ip + ":" + app.Opt.Port + "/#p=" + app.Phones.PairSecret;

 object Snapshot() {
  long now = Util.Ms();
  var addrs = Util.LocalAddrs();
  lock (app.Gate) {
   var devices = app.Hub.Links().Select(l => new {
    id = l.Id, kind = "pc", name = l.M.name, form = l.M.form ?? "desktop", state = l.State, error = l.Error,
    addr = l.Url != null ? new Uri(l.Url).Host : l.M.addrs.FirstOrDefault(), seen = l.M.seen, pending = l.Pending.Count,
    text = l.PeerText, files = l.PeerFiles, paused = l.PeerPaused
   }).OrderBy(x => x.state == "offline" ? 1 : 0).ThenBy(x => x.name).ToList();
   var phones = C.phones.Select(p => new { id = "phone:" + p.id, kind = "phone", name = p.name, form = "phone", state = now - p.seen < 40000 ? "online" : "offline", seen = p.seen }).ToList();
   return new {
    rev = app.Changes.Rev, version = Program.Version,
    me = new { id = C.deviceId, name = C.deviceName, form = Shell.Form, port = app.Opt.Port, ip = addrs.FirstOrDefault()?.Ip.ToString() ?? "127.0.0.1", mdns = app.MdnsName, addrs = addrs.Select(a => new { ip = a.Ip.ToString(), name = a.Name, good = a.Score >= 0 }).ToList() },
    settings = new { C.syncText, C.syncFiles, C.notify, C.paused, C.autostart, C.sendTo, C.receiveFolder, C.autoLimitMB, noShell = app.Opt.NoShell },
    devices, phones,
    nearby = app.Hub.Near.Values.OrderBy(n => n.name).Select(n => new { n.id, n.name, form = n.form ?? "desktop", n.ip, n.port, n.state, n.code, n.error }).ToList(),
    requests = app.Hub.Requests.Where(r => r.status == "pending" && r.until > now).Select(r => new { r.id, r.kind, r.name, form = r.form ?? (r.kind == "phone" ? "phone" : "desktop"), r.code, r.ip, left = (r.until - now) / 1000 }).ToList(),
    feed = app.Feed.Snapshot().Take(80).Select(f => new {
     f.id, f.kind, f.dir, f.from, f.to, text = Util.Clip(f.text, 800), chars = f.text?.Length ?? 0, f.time, f.status, f.note, f.total, f.done, f.image, f.clip,
     files = f.files.Take(12).Select(x => new { x.name, x.size, exists = x.path != null && File.Exists(x.path) }).ToList(), more = Math.Max(0, f.files.Count - 12)
    }).ToList(),
    pair = new { url = PairUrl(addrs.FirstOrDefault()?.Ip.ToString() ?? "127.0.0.1"), until = app.Phones.PairUntil },
    pending = app.PendingSend == null ? null : new { count = app.PendingSend.Length, names = app.PendingSend.Take(3).Select(Path.GetFileName).ToList() },
    scanning = app.Discovery.Scanning, lastScan = app.Discovery.LastScan, hop = app.Discovery.Hop,
    welcomed = C.welcomed, fake = app.Clip.Fake
   };
  }
 }

 // ───────────── 测试（只在 --fake-clipboard 模式下存在） ─────────────

 void Test(Req q, Res res) {
  if (!app.Clip.Fake || !q.Loopback || q.H("X-LB-Local") != app.LocalToken) throw new ApiError(404, "没有这个地址");
  var d = q.Method == "POST" ? q.Json() : new Dictionary<string, object>();
  switch (q.Path) {
   case "/api/test/copy": {
    string kind = d.Get("kind"); var paths = d.List("paths").ToArray();
    app.Clip.FakeKind = kind; app.Clip.FakeText = d.Get("text"); app.Clip.FakePaths = paths;
    app.Hub.LocalCopy(kind, kind == "text" ? d.Get("text") : null, kind == "text" ? null : paths);
    res.Json(new { ok = true }); return;
   }
   case "/api/test/clip": res.Json(new { kind = app.Clip.FakeKind, text = app.Clip.FakeText, paths = app.Clip.FakePaths }); return;
   case "/api/test/info": res.Json(new { id = C.deviceId, tag = app.Hub.Tag, pair = app.Phones.PairSecret, root = app.Root, receive = C.receiveFolder }); return;
  }
  throw new ApiError(404, "没有这个地址");
 }

 // ───────────── 静态文件 ─────────────

 void Static(Req q, Res res) {
  string p = q.Path;
  if (p == "/get" || p == "/LanBridge.exe") {
   var exe = Shell.ExePath; var fi = new FileInfo(exe);
   using var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
   var s = res.Begin(200, "application/vnd.microsoft.portable-executable", fi.Length, new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"LanBridge.exe\"" });
   f.CopyTo(s); s.Flush(); return;
  }
  if (p == "/favicon.ico") { res.Bytes(Embedded.Resource("app.ico"), "image/x-icon"); return; }
  if (p == "/apple-touch-icon.png" || p == "/assets/touch-icon.png") { res.Bytes(Brand.TouchIcon(), "image/png"); return; }
  string file = p == "/" ? "phone.html" : p == "/desk" ? "desk.html" : p.StartsWith("/assets/") ? p.Substring(8) : null;
  if (file == null || file.Contains("/") || file.Contains("..")) throw new ApiError(404, "没有这个页面");
  var bytes = Embedded.Resource("web/" + file) ?? throw new ApiError(404, "没有这个页面");
  string ext = Path.GetExtension(file);
  string type = ext switch { ".html" => "text/html; charset=utf-8", ".js" => "application/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".svg" => "image/svg+xml", ".json" => "application/manifest+json", ".png" => "image/png", _ => "application/octet-stream" };
  var headers = ext == ".html" ? new Dictionary<string, string> { ["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'" } : null;
  res.Bytes(bytes, type, 200, headers);
 }
}
