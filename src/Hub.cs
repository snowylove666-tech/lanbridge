using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LanBridge;

public sealed class Item { public string name; public long size; }

/// <summary>一次要同步的东西：复制了文字 / 图片 / 文件，或者主动发文件（send）。</summary>
public sealed class SyncEvent {
 public string id, origin, originName, kind, text, target, via;
 public long stamp;
 public List<Item> items = new List<Item>();
 public long TotalSize() => items == null ? 0 : items.Sum(i => i.size);
}
sealed class WaitReply { public List<SyncEvent> events = new List<SyncEvent>(); }

sealed class OutEvent {
 public SyncEvent Ev; public string[] Paths; public long Created;
 public readonly HashSet<string> Targets = new HashSet<string>(), Delivered = new HashSet<string>();
 public readonly Dictionary<string, string> Failed = new Dictionary<string, string>();
 public readonly Dictionary<string, long> Sent = new Dictionary<string, long>();
}
sealed class InEvent {
 public SyncEvent Ev; public string From, Mode; public string[] Paths; public bool[] Have;
 public long Active, Created; public bool Done;
}

/// <summary>和另一台电脑的连接状态。每台一个，后台两个线程：一个负责连接和推送，一个负责长轮询拉取。</summary>
sealed class Link {
 public readonly string Id; public Member M;
 public volatile string Url, Error; public volatile bool PushOk, Stopped, ViaRelay;
 public long LastOk, LastIn, LastHello, LastLanTry, Offset; public int Fails;
 public bool PeerText = true, PeerFiles = true, PeerPaused;
 public readonly List<string> Pending = new List<string>();
 public readonly HashSet<string> InFlight = new HashSet<string>();
 public readonly Dictionary<string, long> Hold = new Dictionary<string, long>();
 public readonly Dictionary<string, int> Tries = new Dictionary<string, int>();
 public readonly AutoResetEvent Signal = new AutoResetEvent(false), WaitSignal = new AutoResetEvent(false);
 public Link(Member m) { Id = m.id; M = m; }
 public string State {
  get {
   long now = Util.Ms();
   if (PushOk && now - LastOk < 40000) return "online";
   if (now - LastIn < 45000) return "reverse";
   return "offline";
  }
 }
}

sealed partial class Hub {
 readonly App app; Config C => app.C; object Gate => app.Gate;
 Keys keys;
 public string Tag => keys.Tag;
 readonly Dictionary<string, Link> links = new Dictionary<string, Link>();
 readonly Dictionary<string, OutEvent> outs = new Dictionary<string, OutEvent>();
 readonly Dictionary<string, InEvent> ins = new Dictionary<string, InEvent>();
 readonly Dictionary<string, long> finished = new Dictionary<string, long>();
 readonly Dictionary<string, long> nonces = new Dictionary<string, long>();
 volatile bool running = true;
 long lastLocalCopy, lastApplied;

 /// <summary>这台电脑剪贴板上“最新的一份”（本机复制的或别的设备同步来的），手机网页显示它。</summary>
 public SyncEvent Latest; public string[] LatestPaths;

 public Hub(App app) { this.app = app; keys = new Keys(Convert.FromBase64String(C.familyKey)); }

 public void Start() {
  lock (Gate) foreach (var m in C.members.ToList()) if (!C.blocked.Contains(m.id) && m.id != C.deviceId) StartLink(m);
  Util.Thread("hub-janitor", Janitor);
 }
 public void Stop() { running = false; lock (Gate) foreach (var l in links.Values) { l.Stopped = true; l.Signal.Set(); l.WaitSignal.Set(); } }

 Link StartLink(Member m) {
  var l = new Link(m); links[m.id] = l;
  Util.Thread("link-" + m.id, () => LinkLoop(l));
  Util.Thread("wait-" + m.id, () => WaitLoop(l));
  return l;
 }

 public List<Link> Links() { lock (Gate) return links.Values.ToList(); }
 public Link FindLink(string id) { lock (Gate) return links.TryGetValue(id ?? "", out var l) ? l : null; }

 /// <summary>拿到一台同组电脑（签名有效、广播里标签相同）时调用：没见过就加进来。</summary>
 public Link EnsureLink(string id, string name, string form = null) {
  if (string.IsNullOrEmpty(id) || id == C.deviceId || id.Length > 40) return null;
  lock (Gate) {
   if (C.blocked.Contains(id)) return null;
   if (links.TryGetValue(id, out var l)) return l;
   var m = C.members.FirstOrDefault(x => x.id == id);
   if (m == null) { m = new Member { id = id, name = Util.Clip(name ?? "电脑", 40), form = form, added = Util.Ms() }; C.members.Add(m); app.Save(); }
   l = StartLink(m);
   app.Changes.Bump();
   return l;
  }
 }

 public static void AddAddr(Member m, string ip, int port) {
  if (string.IsNullOrEmpty(ip)) return;
  if (port > 0) m.port = port;
  m.addrs.Remove(ip); m.addrs.Insert(0, ip);
  while (m.addrs.Count > 6) m.addrs.RemoveAt(m.addrs.Count - 1);
 }

 // ───────────── 发出的请求 ─────────────

 HttpWebRequest Signed(string baseUrl, string method, string pq, int timeout) {
  var r = app.Relay.IsMine(baseUrl) ? Relay.Make(baseUrl + pq, method, timeout) : Client.Make(baseUrl + pq, method, timeout);
  string ts = Util.Ms().ToString(), nonce = Crypto.Token(8);
  r.Headers["X-LB-Id"] = C.deviceId; r.Headers["X-LB-Ts"] = ts; r.Headers["X-LB-Nonce"] = nonce;
  r.Headers["X-LB-Port"] = app.Opt.Port.ToString();
  r.Headers["X-LB-Sig"] = Convert.ToBase64String(Crypto.Hmac(keys.Sig, C.deviceId + "\n" + ts + "\n" + nonce + "\n" + method + "\n" + pq));
  return r;
 }
 byte[] CallRaw(string baseUrl, string method, string pq, object body, int timeout) {
  var k = keys;
  var r = Signed(baseUrl, method, pq, timeout);
  var resp = Client.Send(r, body == null ? null : k.Seal(J.Bytes(body)));
  return resp.Length == 0 ? new byte[0] : k.Open(resp);
 }
 Dictionary<string, object> Call(string baseUrl, string method, string pq, object body, int timeout) {
  var b = CallRaw(baseUrl, method, pq, body, timeout); return b.Length == 0 ? new Dictionary<string, object>() : J.Obj(b);
 }

 public object MyInfo() => new {
  lb = 2, id = C.deviceId, name = C.deviceName, port = app.Opt.Port, form = Shell.Form, ver = Program.Version, time = Util.Ms(),
  addrs = app.Opt.NoLan ? new string[0] : app.Opt.Loopback ? new[] { "127.0.0.1" } : Util.LocalAddrs().Select(a => a.Ip.ToString()).Take(6).ToArray(),
  syncText = C.syncText, syncFiles = C.syncFiles, paused = C.paused, relay = C.relay, relayAt = C.relayAt
 };

 /// <summary>改了中转地址：经各条现有的路（可能是旧中转）把新地址告诉组里的电脑。</summary>
 public void AnnounceRelay() {
  var info = MyInfo();
  var tasks = Links().Where(l => l.Url != null).Select(l => Task.Run(() => { try { Call(l.Url, "POST", "/lb/hello", info, 8000); } catch { } })).ToArray();
  try { Task.WaitAll(tasks, 9000); } catch { }
 }

 /// <summary>中转地址在组内同步：谁最后改的听谁的。</summary>
 public void AdoptRelay(Dictionary<string, object> d) {
  long at = d.Num("relayAt");
  if (at <= 0 || !d.ContainsKey("relay")) return;
  bool changed;
  lock (Gate) {
   changed = at > C.relayAt;
   if (changed) { var v = d["relay"] as string; C.relay = v == null ? null : Util.Clip(v.Trim(), 300); C.relayAt = at; }
  }
  if (!changed) return;
  // 组里别的电脑换了中转：这台的临时中转就用不上了，关掉。
  if (C.relayTemp && C.relay != app.Temp.Url) { lock (Gate) C.relayTemp = false; app.Temp.Stop(); }
  app.Save(); app.Relay.Kick(); app.Changes.Bump();
 }

 void ApplyInfo(Link l, Dictionary<string, object> d, string ip) {
  var m = l.M;
  string name = d.Get("name"); if (name.Length > 0) m.name = Util.Clip(name, 40);
  string form = d.Get("form"); if (form.Length > 0) m.form = form;
  int port = (int)d.Num("port", m.port);
  foreach (var a in d.List("addrs").Take(6).Reverse()) if (IPAddress.TryParse(a, out var x) && Util.IsPrivate(x) && !m.addrs.Contains(a)) m.addrs.Add(a);
  if (ip != null) AddAddr(m, ip, port); else if (port > 0) m.port = port;
  while (m.addrs.Count > 6) m.addrs.RemoveAt(m.addrs.Count - 1);
  m.seen = Util.Ms();
  if (d.Num("relayAt") > C.relayAt) Util.Thread("relay-adopt", () => AdoptRelay(d));
  if (d.Flag("syncText") is bool t) l.PeerText = t;
  if (d.Flag("syncFiles") is bool f) l.PeerFiles = f;
  if (d.Flag("paused") is bool p) l.PeerPaused = p;
 }

 // ───────────── 连接与推送 ─────────────

 void LinkLoop(Link l) {
  while (running && !l.Stopped) {
   long now = Util.Ms();
   if (!l.PushOk || now - l.LastHello > 15000) Hello(l);
   string next = null;
   if (l.PushOk) lock (Gate) next = l.Pending.FirstOrDefault(id => !l.InFlight.Contains(id) && !(l.Hold.TryGetValue(id, out var h) && h > Util.Ms()));
   if (next != null) { Push(l, next); continue; }
   int wait = l.PushOk ? 15000 : Math.Min(2000 << Math.Min(l.Fails, 3), 15000);
   l.Signal.WaitOne(wait);
  }
 }

 /// <summary>
 /// 和对方打招呼，顺便找一条能通的路：先试局域网地址（先用很短的 TCP 连接筛一遍），都不通再走中转。
 /// 已经走中转时每分钟再看一次局域网，回到同一个网络就自动换回直连。
 /// </summary>
 void Hello(Link l) {
  long now = Util.Ms(); l.LastHello = now;
  string before = l.State, relay = app.Relay.PeerUrl(l.Id);
  var lan = new List<string>();
  lock (Gate) {
   if (l.Url != null && !l.ViaRelay) lan.Add(l.Url);
   foreach (var a in l.M.addrs) { string u = "http://" + a + ":" + l.M.port; if (!lan.Contains(u)) lan.Add(u); }
  }
  if (app.Opt.NoLan) lan.Clear();
  var tries = new List<(string url, bool via)>();
  if (relay == null) tries.AddRange(lan.Take(7).Select(u => (u, false)));
  else {
   bool onRelay = l.PushOk && l.ViaRelay;
   if (!onRelay || now - l.LastLanTry > 60000) { l.LastLanTry = now; tries.AddRange(Reachable(lan.Take(7).ToList(), 900).Select(u => (u, false))); }
   tries.Add((relay, true));
  }
  string lastError = null;
  foreach (var (url, via) in tries) {
   try {
    long t0 = Util.Ms();
    var d = Call(url, "POST", "/lb/hello", MyInfo(), via ? 12000 : tries.Count > 2 ? 1500 : 2500);
    long t1 = Util.Ms();
    if (d.Get("id") != l.Id) continue;
    lock (Gate) {
     l.Url = url; l.ViaRelay = via; l.PushOk = true; l.LastOk = t1; l.Fails = 0; l.Error = null;
     l.Offset = d.Num("time") - (t0 + t1) / 2;
     ApplyInfo(l, d, via ? null : new Uri(url).Host);
    }
    if (before != l.State) { app.Changes.Bump(); l.WaitSignal.Set(); }
    return;
   } catch (ApiError e) when (e.Status == 401) { lastError = e.Message.Contains("时间") ? e.Message : "密钥不一致，请重新连接这台电脑"; }
   catch (Exception) { }
  }
  lock (Gate) { l.PushOk = false; l.Fails++; l.Error = lastError; }
  if (before != l.State) app.Changes.Bump();
 }

 /// <summary>并行试 TCP 连接，留下能连上的（保持原来的顺序）。另一个网络里的私有地址一般连不上，不用每个都等 HTTP 超时。</summary>
 static List<string> Reachable(List<string> urls, int ms) {
  if (urls.Count == 0) return urls;
  var tasks = urls.Select(u => Task.Run(() => { var x = new Uri(u); return Client.CanConnect(x.Host, x.Port, ms); })).ToArray();
  try { Task.WaitAll(tasks, ms + 600); } catch { }
  return urls.Where((u, i) => tasks[i].Status == TaskStatus.RanToCompletion && tasks[i].Result).ToList();
 }

 void Push(Link l, string id) {
  OutEvent oe;
  lock (Gate) {
   if (!outs.TryGetValue(id, out oe)) { l.Pending.Remove(id); return; }
   l.InFlight.Add(id);
  }
  try {
   if (l.ViaRelay && oe.Ev.items.Any(x => Keys.SealedLength(x.size) > app.Relay.MaxBody)) throw new ApiError(413, "经中转一次最多传 " + (app.Relay.MaxBody >> 20) + " MB，大文件请在同一个网络里传");
   var r = Call(l.Url, "POST", "/lb/offer", new { ev = oe.Ev }, l.ViaRelay ? 20000 : 10000);
   if (r.Flag("done") == true) { Delivered(l, oe, r.Get("skipped")); return; }
   if (r.Flag("busy") == true) { lock (Gate) l.Hold[id] = Util.Ms() + 20000; return; }
   long before = 0;
   var need = r.List("need").Select(int.Parse).ToList();
   for (int i = 0; i < oe.Ev.items.Count; i++) {
    if (need.Contains(i)) Upload(l, oe, i, before);
    before += oe.Ev.items[i].size;
   }
   var c = Call(l.Url, "POST", "/lb/commit", new { id }, l.ViaRelay ? 40000 : 20000);
   if (c.Flag("done") == true) Delivered(l, oe, null);
  } catch (ApiError e) when (e.Status >= 400 && e.Status < 500 && e.Status != 401 && e.Status != 409) {
   Failed(l, oe, e.Message);
  } catch (Exception e) {
   int tries;
   lock (Gate) { l.Tries.TryGetValue(id, out tries); l.Tries[id] = ++tries; l.Hold[id] = Util.Ms() + 3000 * tries; }
   Log.Write("推送到 " + l.M.name + " 失败（第 " + tries + " 次）：" + e.Message);
   if (tries >= 6) Failed(l, oe, "多次发送失败：" + e.Message);
   else Hello(l);
  } finally { lock (Gate) l.InFlight.Remove(id); }
 }

 void Upload(Link l, OutEvent oe, int i, long before) {
  string path = oe.Paths[i]; long size = oe.Ev.items[i].size;
  var fi = new FileInfo(path);
  if (!fi.Exists || fi.Length != size) throw new ApiError(410, "文件已被移动或修改：" + oe.Ev.items[i].name);
  var r = Signed(l.Url, "PUT", "/lb/blob?ev=" + oe.Ev.id + "&i=" + i, 60000);
  r.ContentType = "application/octet-stream"; r.ContentLength = Keys.SealedLength(size);
  r.AllowWriteStreamBuffering = false; r.SendChunked = false;
  using (var s = r.GetRequestStream()) using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
   keys.SealStream(f, size, s, n => SentProgress(l, oe, before + n));
  Client.Send(r, null);
 }

 void SentProgress(Link l, OutEvent oe, long n) {
  long sum; lock (Gate) { oe.Sent[l.Id] = n; sum = oe.Sent.Values.Sum(); }
  app.Feed.Progress(oe.Ev.id, sum);
 }

 void Delivered(Link l, OutEvent oe, string skipped) {
  bool all;
  lock (Gate) {
   l.Pending.Remove(oe.Ev.id); l.Hold.Remove(oe.Ev.id); l.Tries.Remove(oe.Ev.id);
   if (!string.IsNullOrEmpty(skipped)) oe.Failed[l.Id] = skipped; else oe.Delivered.Add(l.Id);
   oe.Sent[l.Id] = oe.Ev.TotalSize();
   all = oe.Targets.All(t => oe.Delivered.Contains(t) || oe.Failed.ContainsKey(t));
  }
  UpdateOutFeed(oe, all);
 }

 void Failed(Link l, OutEvent oe, string why) {
  bool all;
  lock (Gate) {
   l.Pending.Remove(oe.Ev.id); l.Hold.Remove(oe.Ev.id); l.Tries.Remove(oe.Ev.id);
   oe.Failed[l.Id] = why;
   all = oe.Targets.All(t => oe.Delivered.Contains(t) || oe.Failed.ContainsKey(t));
  }
  Log.Write("没送到 " + l.M.name + "：" + why);
  UpdateOutFeed(oe, all);
 }

 void UpdateOutFeed(OutEvent oe, bool all) {
  string status, note; int ok, bad;
  lock (Gate) { ok = oe.Delivered.Count; bad = oe.Failed.Count; }
  if (!all) { status = "sending"; note = ok > 0 ? "已送到 " + ok + " 台" : null; }
  else if (bad == 0) { status = "done"; note = null; }
  else if (ok > 0) { status = "done"; note = "有 " + bad + " 台没送到：" + oe.Failed.Values.First(); }
  else { status = "failed"; note = oe.Failed.Values.FirstOrDefault(); }
  app.Feed.Update(oe.Ev.id, f => { f.status = status; f.note = note; if (all) f.done = f.total; });
 }

 // ───────────── 长轮询拉取（对方推不过来时走这条路） ─────────────

 void WaitLoop(Link l) {
  while (running && !l.Stopped) {
   if (!l.PushOk || l.Url == null) { l.WaitSignal.WaitOne(2000); continue; }
   try {
    var raw = CallRaw(l.Url, "GET", "/lb/wait", null, 40000);
    var reply = J.Parse<WaitReply>(raw);
    foreach (var ev in reply?.events ?? new List<SyncEvent>()) {
     var e = ev;
     if (e.items != null && e.items.Count > 0) Util.Thread("pull-" + e.id, () => Pull(l, e)); else Pull(l, e);
    }
   } catch (Exception) { if (running && !l.Stopped) Thread.Sleep(2500); }
  }
 }

 void Pull(Link l, SyncEvent ev) {
  string st; InEvent ie;
  try { ie = Claim(l, ev, "pull", out st); } catch (ApiError e) { Log.Write("拒收 " + ev.id + "：" + e.Message); Ack(l, ev.id); return; }
  if (st == "done") { Ack(l, ev.id); return; }
  if (st == "busy" || ie == null) return;
  try {
   for (int i = 0; i < ie.Have.Length; i++) if (!ie.Have[i]) Download(l, ie, i);
   Finish(ie);
   Ack(l, ev.id);
  } catch (Exception e) {
   lock (Gate) ie.Active = 0;
   Log.Write("从 " + l.M.name + " 下载失败：" + e.Message);
   app.Feed.Update(ev.id, f => { f.status = "failed"; f.note = "下载中断，会自动重试"; });
  }
 }

 void Ack(Link l, string id) { try { Call(l.Url, "POST", "/lb/ack", new { ids = new[] { id } }, 8000); } catch { } }

 void Download(Link l, InEvent ie, int i) {
  var r = Signed(l.Url, "GET", "/lb/blob?ev=" + ie.Ev.id + "&i=" + i, 60000);
  HttpWebResponse resp;
  try { resp = (HttpWebResponse)r.GetResponse(); }
  catch (WebException e) when (e.Response is HttpWebResponse er) { er.Dispose(); throw new ApiError((int)er.StatusCode, "对方没有这个文件了"); }
  using (resp) using (var s = resp.GetResponseStream()) Receive(ie, i, s, resp.ContentLength);
 }

 /// <summary>收一个文件：先写 .lbpart，解密校验都通过才改成正式文件名。</summary>
 void Receive(InEvent ie, int i, Stream s, long sealedLen) {
  var item = ie.Ev.items[i];
  if (sealedLen != Keys.SealedLength(item.size)) throw new ApiError(400, "文件长度不对");
  string final = ie.Paths[i], part = final + ".lbpart";
  Directory.CreateDirectory(Path.GetDirectoryName(final));
  long before = 0; for (int k = 0; k < i; k++) before += ie.Ev.items[k].size;
  try {
   using (var f = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) {
    keys.OpenStream(s, sealedLen, f, n => { ie.Active = Util.Ms(); app.Feed.Progress(ie.Ev.id, before + n); });
    if (f.Length != item.size) throw new ApiError(400, "文件大小不对");
   }
   if (File.Exists(final)) File.Delete(final);
   File.Move(part, final);
   lock (Gate) ie.Have[i] = true;
  } catch { try { File.Delete(part); } catch { } throw; }
 }

 // ───────────── 收到的事件 ─────────────

 InEvent Claim(Link l, SyncEvent ev, string mode, out string status) {
  FeedItem add = null; InEvent ie;
  lock (Gate) {
   long now = Util.Ms();
   if (ins.TryGetValue(ev.id ?? "", out ie)) {
    if (ie.Done) { status = "done"; return ie; }
    if (ie.Mode != mode && now - ie.Active < 30000) { status = "busy"; return ie; }
    ie.Mode = mode; ie.Active = now; status = "go"; return ie;
   }
   if (finished.ContainsKey(ev.id ?? "")) { status = "done"; return null; }
   Validate(l, ev);
   ie = new InEvent { Ev = ev, From = l.Id, Mode = mode, Active = now, Created = now, Have = new bool[ev.items.Count] };
   ie.Paths = Place(ev);
   ins[ev.id] = ie;
   if (ev.kind != "text") add = new FeedItem {
    id = ev.id, kind = ev.kind, dir = "in", from = ev.originName, status = "receiving", clip = ev.kind != "send", image = ev.kind == "image",
    total = ev.TotalSize(), files = ev.items.Select((x, k) => new FeedFile { name = Path.GetFileName(ie.Paths[k]), size = x.size, path = ie.Paths[k] }).ToList()
   };
  }
  if (add != null) { add.to.Add(C.deviceName); app.Feed.Add(add); }
  status = "go"; return ie;
 }

 void Validate(Link l, SyncEvent ev) {
  if (string.IsNullOrEmpty(ev.id) || ev.id.Length > 40 || ev.origin != l.Id) throw new ApiError(400, "事件无效");
  ev.items ??= new List<Item>();
  if (!new[] { "text", "image", "files", "send" }.Contains(ev.kind)) throw new ApiError(400, "不认识的内容类型");
  if (ev.kind == "text" && (ev.text == null || ev.text.Length > 4 * 1024 * 1024)) throw new ApiError(400, "文字为空或过长");
  if (ev.kind != "text" && (ev.items.Count == 0 || ev.items.Count > 200 || ev.items.Any(x => x.size < 0 || x.size > 64L << 30))) throw new ApiError(400, "文件列表无效");
  if (ev.kind == "send" && ev.target != C.deviceId) throw new ApiError(400, "不是发给这台电脑的");
  ev.originName = Util.Clip(string.IsNullOrEmpty(ev.originName) ? l.M.name : ev.originName, 60);
 }

 string[] Place(SyncEvent ev) {
  string dir = ev.kind == "send" ? C.receiveFolder : Path.Combine(app.Root, "clip", ev.id);
  Directory.CreateDirectory(dir);
  return ev.items.Select(x => Util.Reserve(dir, Util.SafeName(x.name))).ToArray();
 }

 /// <summary>全部到齐：写剪贴板或提醒收到文件。</summary>
 void Finish(InEvent ie) {
  Link from;
  lock (Gate) {
   if (ie.Done) return;
   ie.Done = true; finished[ie.Ev.id] = Util.Ms();
   links.TryGetValue(ie.From, out from);
  }
  foreach (var p in ie.Paths) Util.Release(p);
  var ev = ie.Ev;
  long local = ev.stamp - (from?.Offset ?? 0);
  string note = null; bool applied = false;
  if (ev.kind == "send") {
   int n = ie.Paths.Length;
   note = "已存到“" + Path.GetFileName(C.receiveFolder.TrimEnd('\\')) + "”文件夹";
   if (C.notify) app.Notify("收到 " + (n == 1 ? Path.GetFileName(ie.Paths[0]) : n + " 个文件"), "来自 " + ev.originName + "，点这里打开", () => Shell.Reveal(ie.Paths[0]));
  } else {
   bool allowed = !C.paused && (ev.kind == "text" ? C.syncText : C.syncFiles);
   bool newer;
   lock (Gate) newer = local >= lastLocalCopy - 300 && local >= lastApplied;
   if (allowed && newer) {
    applied = ev.kind switch {
     "text" => app.Clip.WriteText(ev.text),
     "image" => app.Clip.WriteImage(ie.Paths[0]),
     _ => app.Clip.WriteFiles(ie.Paths),
    };
    if (applied) { lock (Gate) { lastApplied = local; Latest = ev; LatestPaths = ie.Paths; } app.Phones.ClipChanged(); }
    note = applied ? (ev.kind == "files" ? "已放进剪贴板，可以在文件夹里粘贴" : "已放进剪贴板，可以直接粘贴") : "剪贴板被别的程序占用，没写进去";
   } else note = !allowed ? "同步已关闭，只保存了一份" : "你之后又复制了别的，没有覆盖剪贴板";
  }
  if (ev.kind == "text") {
   app.Feed.Add(new FeedItem { id = ev.id, kind = "text", dir = "in", from = ev.originName, to = new List<string> { C.deviceName }, text = ev.text, status = applied ? "done" : "skipped", note = note, clip = true, total = ev.text.Length });
  } else {
   app.Feed.Update(ev.id, f => { f.status = "done"; f.note = note; f.done = f.total; });
  }
 }

 // ───────────── 对方发来的请求（Router 调用，签名已验证） ─────────────

 public Link Verify(Req q) {
  string id = q.H("X-LB-Id"), ts = q.H("X-LB-Ts"), nonce = q.H("X-LB-Nonce"), sig = q.H("X-LB-Sig");
  if (id.Length == 0 || nonce.Length == 0 || nonce.Length > 64) throw new ApiError(401, "缺少签名");
  if (!long.TryParse(ts, out var t) || Math.Abs(Util.Ms() - t) > 10 * 60 * 1000) throw new ApiError(401, "两台电脑的时间相差超过 10 分钟，请校准系统时间");
  byte[] want = Crypto.Hmac(keys.Sig, id + "\n" + ts + "\n" + nonce + "\n" + q.Method + "\n" + q.Target);
  byte[] got; try { got = Convert.FromBase64String(sig); } catch { got = null; }
  if (!Crypto.Same(want, got)) throw new ApiError(401, "不是同一组设备");
  lock (Gate) {
   long now = Util.Ms();
   if (nonces.ContainsKey(id + nonce)) throw new ApiError(401, "重复的请求");
   nonces[id + nonce] = now;
   if (nonces.Count > 5000) foreach (var k in nonces.Where(x => now - x.Value > 11 * 60 * 1000).Select(x => x.Key).ToList()) nonces.Remove(k);
  }
  var l = EnsureLink(id, null) ?? throw new ApiError(403, "这台电脑已被移出设备组");
  lock (Gate) {
   l.LastIn = Util.Ms(); l.M.seen = l.LastIn;
   if (q.Via == null && !app.Opt.NoLan && int.TryParse(q.H("X-LB-Port"), out int port) && (Util.IsPrivate(q.Remote) && !IPAddress.IsLoopback(q.Remote) || app.Opt.Loopback)) AddAddr(l.M, q.Remote.ToString(), port);
  }
  return l;
 }

 public object HelloIn(Link l, Dictionary<string, object> d, Req q) {
  string before = l.State;
  lock (Gate) ApplyInfo(l, d, q.Via == null && !app.Opt.NoLan && Util.IsPrivate(q.Remote) ? q.Remote.ToString() : null);
  // 对方能连到我，我却没连上它：立刻反过来试一次。
  if (!l.PushOk) l.Signal.Set();
  if (before != l.State) app.Changes.Bump();
  return MyInfo();
 }

 public object WaitIn(Link l) {
  long end = Util.Ms() + 25000;
  lock (Gate) {
   while (running) {
    long now = Util.Ms(); l.LastIn = now;
    var list = l.Pending.Where(id => outs.ContainsKey(id) && !l.InFlight.Contains(id)
     && (!l.PushOk || now - outs[id].Created > 8000)
     && !(l.Hold.TryGetValue(id, out var h) && h > now)).Take(20).ToList();
    if (list.Count > 0) {
     foreach (var id in list) l.Hold[id] = now + 60000;
     return new { events = list.Select(id => outs[id].Ev).ToList() };
    }
    int left = (int)(end - now); if (left <= 0) break;
    Monitor.Wait(Gate, Math.Min(left, 1000));
   }
  }
  return new { events = new SyncEvent[0] };
 }

 public object AckIn(Link l, Dictionary<string, object> d) {
  foreach (var id in d.List("ids").Take(50)) {
   OutEvent oe; lock (Gate) outs.TryGetValue(id, out oe);
   if (oe != null && l.Pending.Contains(id)) Delivered(l, oe, null);
  }
  return new { ok = true };
 }

 public object OfferIn(Link l, byte[] body) {
  var wrap = J.Parse<Dictionary<string, SyncEvent>>(body);
  if (wrap == null || !wrap.TryGetValue("ev", out var ev) || ev == null) throw new ApiError(400, "缺少内容");
  ev.items ??= new List<Item>();
  if (C.paused) return new { done = true, skipped = "对方暂停了同步" };
  if ((ev.kind == "text" && !C.syncText) || ((ev.kind == "image" || ev.kind == "files") && !C.syncFiles)) return new { done = true, skipped = "对方关闭了这类内容的同步" };
  var ie = Claim(l, ev, "push", out var st);
  if (st == "done") return new { done = true };
  if (st == "busy") return new { busy = true };
  var need = new List<int>(); lock (Gate) for (int i = 0; i < ie.Have.Length; i++) if (!ie.Have[i]) need.Add(i);
  if (need.Count == 0) { Finish(ie); return new { done = true }; }
  return new { need };
 }

 public object BlobIn(Link l, Req q) {
  string id = q.Q("ev"); int.TryParse(q.Q("i"), out int i);
  InEvent ie; lock (Gate) ins.TryGetValue(id, out ie);
  if (ie == null || ie.From != l.Id || i < 0 || i >= ie.Have.Length) throw new ApiError(404, "没有这份内容");
  if (ie.Done || ie.Have[i]) { q.Body.CopyTo(Stream.Null); return new { ok = true }; }
  lock (Gate) { ie.Mode = "push"; ie.Active = Util.Ms(); }
  Receive(ie, i, q.Body, q.Length);
  return new { ok = true };
 }

 public object CommitIn(Link l, Dictionary<string, object> d) {
  InEvent ie; lock (Gate) ins.TryGetValue(d.Get("id"), out ie);
  if (ie == null || ie.From != l.Id) throw new ApiError(404, "没有这份内容");
  if (!ie.Done) { lock (Gate) if (ie.Have.Any(x => !x)) return new { done = false }; Finish(ie); }
  return new { done = true };
 }

 public void BlobOut(Link l, Req q, Res res) {
  string id = q.Q("ev"); int.TryParse(q.Q("i"), out int i);
  OutEvent oe; lock (Gate) outs.TryGetValue(id, out oe);
  if (oe == null || !oe.Targets.Contains(l.Id) || i < 0 || i >= oe.Paths.Length) throw new ApiError(404, "这份内容已过期");
  long size = oe.Ev.items[i].size, before = 0; for (int k = 0; k < i; k++) before += oe.Ev.items[k].size;
  var fi = new FileInfo(oe.Paths[i]);
  if (!fi.Exists || fi.Length != size) throw new ApiError(410, "文件已被移动或修改");
  using var f = new FileStream(oe.Paths[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
  var s = res.Begin(200, "application/octet-stream", Keys.SealedLength(size));
  keys.SealStream(f, size, s, n => SentProgress(l, oe, before + n));
  s.Flush();
 }

 // ───────────── 本机产生的内容 ─────────────

 /// <summary>经中转时自动同步的上限（复制的图片、文件）；更大的要主动发。</summary>
 const long RelayAutoLimit = 20L << 20;

 /// <summary>本机复制了东西（或手机发来的东西写进了本机剪贴板）。</summary>
 public void LocalCopy(string kind, string text, string[] paths, string via = null) {
  var ev = new SyncEvent { id = Crypto.Token(12), origin = C.deviceId, originName = C.deviceName, kind = kind, text = kind == "text" ? text : null, stamp = Util.Ms(), via = via };
  if (paths != null) foreach (var p in paths) ev.items.Add(new Item { name = Path.GetFileName(p), size = new FileInfo(p).Length });
  long total = ev.TotalSize();
  bool tooBig = kind != "text" && total > (long)C.autoLimitMB * 1024 * 1024;
  List<Link> targets; int relaySkipped;
  lock (Gate) {
   if (kind == "text" && Latest?.kind == "text" && Latest.text == text && Util.Ms() - lastLocalCopy < 1500) return;
   lastLocalCopy = ev.stamp; Latest = ev; LatestPaths = paths;
   // 剪贴板只发给此刻在线的电脑；不在线的等它上线后不补发旧内容（避免突然覆盖它的剪贴板）。
   targets = C.paused || tooBig ? new List<Link>() : links.Values.Where(l => kind == "text" ? C.syncText && l.PeerText : C.syncFiles && l.PeerFiles).Where(l => !l.PeerPaused && l.State != "offline").ToList();
   relaySkipped = kind != "text" && total > RelayAutoLimit ? targets.RemoveAll(l => l.ViaRelay) : 0;
   var oe = new OutEvent { Ev = ev, Paths = paths ?? new string[0], Created = ev.stamp };
   foreach (var l in targets) { oe.Targets.Add(l.Id); l.Pending.Add(ev.id); }
   outs[ev.id] = oe;
   Monitor.PulseAll(Gate);
  }
  foreach (var l in targets) l.Signal.Set();
  app.Phones.ClipChanged();
  if (targets.Count > 0 || via != null || tooBig || relaySkipped > 0) {
   app.Feed.Add(new FeedItem {
    id = ev.id, kind = kind, dir = "out", from = via ?? C.deviceName, to = targets.Select(l => l.M.name).ToList(),
    text = text, image = kind == "image", clip = true, status = targets.Count > 0 ? "sending" : "local", total = total * Math.Max(1, targets.Count),
    files = (paths ?? new string[0]).Select(p => new FeedFile { name = Path.GetFileName(p), path = p, size = new FileInfo(p).Length }).ToList(),
    note = tooBig ? "超过 " + C.autoLimitMB + " MB，没有自动同步。可以拖到设备上发送"
     : relaySkipped > 0 ? "不在同一个网络的电脑超过 " + (RelayAutoLimit >> 20) + " MB 不自动同步，可以拖到设备上发送"
     : targets.Count == 0 && via != null ? "已放进本机剪贴板" : null
   });
  }
 }

 /// <summary>主动发文件给某台电脑。对方不在线就排队，上线后自动送达（24 小时内）。</summary>
 public void SendFiles(string targetId, string[] paths) {
  var l = FindLink(targetId) ?? throw new ApiError(404, "找不到这台设备");
  var files = new List<string>();
  string feedId = Crypto.Token(12);
  var dirs = paths.Where(Directory.Exists).ToList();
  if (dirs.Count > 0) app.Feed.Add(new FeedItem { id = feedId, kind = "send", dir = "out", from = C.deviceName, to = new List<string> { l.M.name }, status = "sending", note = "正在打包文件夹…" });
  Util.Thread("send", () => {
   try {
    foreach (var p in paths) {
     if (Directory.Exists(p)) files.Add(Shell.ZipFolder(p, Path.Combine(app.Root, "stage")));
     else if (File.Exists(p)) files.Add(Path.GetFullPath(p));
    }
    if (files.Count == 0) throw new ApiError(400, "没有可发送的文件");
    var ev = new SyncEvent { id = feedId, origin = C.deviceId, originName = C.deviceName, kind = "send", target = targetId, stamp = Util.Ms() };
    foreach (var p in files) ev.items.Add(new Item { name = Path.GetFileName(p), size = new FileInfo(p).Length });
    var oe = new OutEvent { Ev = ev, Paths = files.ToArray(), Created = ev.stamp };
    oe.Targets.Add(l.Id);
    lock (Gate) { outs[ev.id] = oe; l.Pending.Add(ev.id); Monitor.PulseAll(Gate); }
    var item = new FeedItem { id = ev.id, kind = "send", dir = "out", from = C.deviceName, to = new List<string> { l.M.name }, status = "sending", total = ev.TotalSize(), files = files.Select(p => new FeedFile { name = Path.GetFileName(p), path = p, size = new FileInfo(p).Length }).ToList(), note = l.State == "offline" ? "对方不在线，上线后自动送达" : null };
    if (app.Feed.Find(ev.id) != null) app.Feed.Update(ev.id, f => { f.total = item.total; f.files = item.files; f.note = item.note; }); else app.Feed.Add(item);
    l.Signal.Set();
   } catch (Exception e) {
    app.Feed.Update(feedId, f => { f.status = "failed"; f.note = e.Message; });
    if (app.Feed.Find(feedId) == null) app.Feed.Add(new FeedItem { id = feedId, kind = "send", dir = "out", from = C.deviceName, to = new List<string> { l.M.name }, status = "failed", note = e.Message });
   }
  });
 }

 /// <summary>把动态里的某条重新放进剪贴板（也会同步出去）。</summary>
 public bool Recopy(string id) {
  var f = app.Feed.Find(id); if (f == null) return false;
  if (f.kind == "text" && f.text != null) return app.Clip.WriteText(f.text, true);
  var paths = f.files.Where(x => x.path != null && File.Exists(x.path)).Select(x => x.path).ToArray();
  if (paths.Length == 0) return false;
  return f.image && paths.Length == 1 ? app.Clip.WriteImage(paths[0], true) : app.Clip.WriteFiles(paths, true);
 }

 public void Retry(string id) {
  lock (Gate) {
   if (!outs.TryGetValue(id, out var oe)) return;
   foreach (var t in oe.Failed.Keys.ToList()) if (links.TryGetValue(t, out var l)) { oe.Failed.Remove(t); l.Pending.Add(id); l.Tries.Remove(id); l.Hold.Remove(id); l.Signal.Set(); }
  }
  app.Feed.Update(id, f => { f.status = "sending"; f.note = null; });
 }

 // ───────────── 清理 ─────────────

 void Janitor() {
  string lastSig = "";
  int tick = 0;
  while (running) {
   Thread.Sleep(2000); tick++;
   long now = Util.Ms();
   var expired = new List<(Link, OutEvent, string)>();
   lock (Gate) {
    foreach (var l in links.Values)
     foreach (var id in l.Pending.ToList()) {
      if (!outs.TryGetValue(id, out var oe)) { l.Pending.Remove(id); continue; }
      long age = now - oe.Created;
      if (oe.Ev.kind != "send" && age > 3 * 60 * 1000) expired.Add((l, oe, "对方一直不在线，剪贴板内容已过期"));
      else if (oe.Ev.kind == "send" && age > 24 * 3600 * 1000) expired.Add((l, oe, "对方 24 小时没上线，已取消"));
     }
    if (tick % 30 == 0) {
     foreach (var id in outs.Where(x => now - x.Value.Created > 3600 * 1000 && !links.Values.Any(l => l.Pending.Contains(x.Key)) && x.Value.Ev != Latest).Select(x => x.Key).ToList()) outs.Remove(id);
     foreach (var id in ins.Where(x => x.Value.Done && now - x.Value.Created > 600 * 1000).Select(x => x.Key).ToList()) ins.Remove(id);
     foreach (var id in ins.Where(x => !x.Value.Done && now - x.Value.Active > 3600 * 1000).Select(x => x.Key).ToList()) { foreach (var p in ins[id].Paths) Util.Release(p); ins.Remove(id); }
     foreach (var id in finished.Where(x => now - x.Value > 2 * 86400 * 1000).Select(x => x.Key).ToList()) finished.Remove(id);
    }
   }
   foreach (var (l, oe, why) in expired) Failed(l, oe, why);
   if (tick % 900 == 5) Shell.CleanOld(Path.Combine(app.Root, "clip"), TimeSpan.FromDays(3), Latest?.id);
   if (tick % 900 == 6) Shell.CleanOld(Path.Combine(app.Root, "stage"), TimeSpan.FromDays(2), null);
   app.Discovery.Expire();
   string sig = string.Join(",", Links().Select(l => l.Id + l.State));
   if (sig != lastSig) { lastSig = sig; app.Changes.Bump(); }
  }
 }
}
