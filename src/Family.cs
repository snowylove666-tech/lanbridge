using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

namespace LanBridge;

/// <summary>附近发现的、还不在同一组里的电脑。</summary>
sealed class Nearby {
 public string id, name, form, ip, tag, state = "idle", code, error; public int port; public long seen;
 /// <summary>经中转配对时的地址（中转/c/配对码）；局域网里发现的为 null。</summary>
 public string url;
 /// <summary>连接码里带的中转地址（临时中转、自己的中转）：连上后这台电脑也用它。</summary>
 public string relayBase;
}

/// <summary>别人请求加入（电脑或手机）。</summary>
sealed class JoinReq {
 public string id, kind, name, form, code, ip, peerId, status = "pending", token; public int port; public long until;
 public byte[] shared, payload;
}

/// <summary>设备组：发现、配对（ECDH + 核对码）、合并、移除。</summary>
sealed partial class Hub {
 public readonly Dictionary<string, Nearby> Near = new Dictionary<string, Nearby>();
 public readonly List<JoinReq> Requests = new List<JoinReq>();

 public byte[] Seal(object o) => keys.Seal(J.Bytes(o));
 public byte[] Open(byte[] b) => keys.Open(b);

 public object PublicHello() => new { lb = 2, app = "LanBridge", id = C.deviceId, name = C.deviceName, port = app.Opt.Port, tag = Tag, form = Shell.Form, ver = Program.Version, time = Util.Ms() };

 /// <summary>广播或扫描看到一台轻桥。返回 true 表示第一次见到。</summary>
 public bool Seen(Dictionary<string, object> h, string ip) {
  string id = h.Get("id"); if (id.Length == 0 || id == C.deviceId || id.Length > 40) return false;
  int port = (int)h.Num("port", 49321); if (port < 1 || port > 65535) return false;
  string name = Util.Clip(h.Get("name"), 40), form = h.Get("form");
  if (h.Get("tag") == Tag) {
   bool known; lock (Gate) known = links.ContainsKey(id);
   var l = EnsureLink(id, name, form); if (l == null) return false;
   bool newAddr;
   lock (Gate) { newAddr = !l.M.addrs.Contains(ip) || l.M.port != port; AddAddr(l.M, ip, port); if (name.Length > 0) l.M.name = name; Near.Remove(id); if (l.ViaRelay) l.LastLanTry = 0; }
   if (l.ViaRelay) newAddr = true;
   if (l.State == "offline" || newAddr) l.Signal.Set();
   return !known;
  }
  bool fresh;
  lock (Gate) {
   fresh = !Near.TryGetValue(id, out var n);
   if (fresh) { n = new Nearby { id = id }; Near[id] = n; }
   n.name = name.Length > 0 ? name : "电脑"; n.ip = ip; n.port = port; n.form = form; n.tag = h.Get("tag"); n.seen = Util.Ms();
  }
  if (fresh) app.Changes.Bump();
  return fresh;
 }

 // ───────────── 我去连别人 ─────────────

 public void Connect(string nearbyId) {
  Nearby n; lock (Gate) Near.TryGetValue(nearbyId ?? "", out n);
  if (n == null) throw new ApiError(404, "这台电脑已经不在附近了，请刷新");
  if (n.state == "asking" || n.state == "waiting") return;
  n.state = "asking"; n.error = null; n.code = null; n.seen = Util.Ms(); app.Changes.Bump();
  Util.Thread("join", () => JoinFlow(n));
 }

 /// <summary>
 /// 不在同一个网络：输入另一台电脑显示的连接码，经中转请求连接。
 /// 连接码是 8 位配对码，用临时中转或自己的中转时后面带“@中转地址”，这台电脑不用先设置中转。
 /// </summary>
 public void ConnectCode(string text) {
  text = (text ?? "").Trim();
  string relayBase = null; int at = text.IndexOf('@');
  if (at >= 0) {
   relayBase = Relay.FromShortHost(text.Substring(at + 1));
   if (relayBase == null) throw new ApiError(400, "连接码 @ 后面的中转地址不对");
   text = text.Substring(0, at);
  }
  string code = new string(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
  if (code.Length != 8) throw new ApiError(400, "连接码是 8 位，例如 K7M2-PQ9X（后面可能还带着 @ 和中转地址，整个粘贴进来）");
  string b = relayBase ?? app.Relay.Base;
  if (b == null) throw new ApiError(400, "这台电脑还没开跨网络。把对方连接码整个粘贴进来（带 @ 后面的部分），或先开启跨网络");
  if (code == app.Relay.Code && app.Relay.CodeActive) throw new ApiError(400, "这是本机的连接码，要在另一台电脑上输入");
  var n = new Nearby { id = "code-" + code, name = "连接码 " + code.Substring(0, 4) + "-" + code.Substring(4), ip = "经中转服务器", url = b + "/c/" + code, port = 0, seen = Util.Ms(), relayBase = relayBase != null && relayBase != app.Relay.Base ? relayBase : null };
  lock (Gate) { if (Near.TryGetValue(n.id, out var old) && (old.state == "asking" || old.state == "waiting")) return; Near[n.id] = n; }
  Connect(n.id);
 }

 /// <summary>手动输入地址：192.168.1.8 或 192.168.1.8:49321。</summary>
 public void ConnectAddress(string text) {
  text = (text ?? "").Trim();
  if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7);
  text = text.TrimEnd('/');
  int port = 49321; string host = text;
  int c = text.LastIndexOf(':'); if (c > 0 && int.TryParse(text.Substring(c + 1), out var p)) { host = text.Substring(0, c); port = p; }
  if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) {
   try { ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); } catch { ip = null; }
   if (ip == null) throw new ApiError(400, "地址不对，填对方电脑轻桥里显示的地址，例如 192.168.1.8");
  }
  if (!Util.IsPrivate(ip)) throw new ApiError(400, "只能连局域网地址（192.168.x.x、10.x.x.x 这类）");
  Dictionary<string, object> h;
  try { h = J.Obj(Client.Send(Client.Make("http://" + ip + ":" + port + "/lb/hello", "GET", 3000), null)); }
  catch { throw new ApiError(404, "连不上 " + ip + ":" + port + "。请确认那台电脑开着轻桥，并且允许了防火墙"); }
  if (h.Num("lb") != 2) throw new ApiError(400, "那个地址不是轻桥 2");
  Seen(h, ip.ToString());
  if (h.Get("tag") == Tag) return;
  Connect(h.Get("id"));
 }

 public void CancelConnect(string nearbyId) {
  lock (Gate) if (Near.TryGetValue(nearbyId ?? "", out var n)) { n.state = "idle"; n.code = null; }
  app.Changes.Bump();
 }

 void JoinFlow(Nearby n) {
  string url = n.url ?? "http://" + n.ip + ":" + n.port;
  bool viaRelay = n.url != null;
  HttpWebRequest Make(string u) => viaRelay ? Relay.Make(u, "POST", 15000) : Client.Make(u, "POST", 5000);
  try {
   using var hs = new Handshake();
   var body = J.Bytes(new { id = C.deviceId, name = C.deviceName, form = Shell.Form, port = app.Opt.Port, pub = hs.Public, tag = Tag });
   var d = J.Obj(Client.Send(Make(url + "/lb/join"), body, "application/json"));
   if (d.Get("status") == "member") {
    if (viaRelay) {
     lock (Gate) Near.Remove(n.id);
     // 已经在同一组（比如临时中转换了地址）：改用连接码里的中转，马上重新连上。
     if (n.relayBase != null) UseRelay(n.relayBase);
     app.Feed.Add(new FeedItem { id = Crypto.Token(8), kind = "info", dir = "in", status = "done", note = n.relayBase != null ? "已经在同一个设备组里，改用新的中转地址重新连上“" + d.Get("name") + "”" : "“" + d.Get("name") + "”已经在设备组里了" });
     app.Changes.Bump();
    }
    else n.state = "idle";
    app.Discovery.Kick(); return;
   }
   if (viaRelay && d.Get("name").Length > 0) n.name = Util.Clip(d.Get("name"), 40);
   var shared = hs.Derive(d.Get("pub"));
   n.code = Handshake.Code(shared); n.state = "waiting"; app.Changes.Bump();
   string req = d.Get("req"); long end = Util.Ms() + 125000;
   while (Util.Ms() < end && n.state == "waiting") {
    Thread.Sleep(900);
    Dictionary<string, object> p;
    try { p = J.Obj(Client.Send(Make(url + "/lb/join/poll"), J.Bytes(new { req }), "application/json")); }
    catch (ApiError) { throw; } catch { continue; }
    switch (p.Get("status")) {
     case "pending": continue;
     case "ok":
      var payload = J.Obj(new Keys(shared).Open(Convert.FromBase64String(p.Get("data"))));
      Adopt(payload);
      // 经连接码里的中转连上的：以后也用这个中转（组里的设置）。
      if (n.relayBase != null) { string r = payload.Get("relay"); UseRelay(r.Length > 0 ? r : n.relayBase); }
      lock (Gate) Near.Remove(n.id);
      app.Feed.Add(new FeedItem { id = Crypto.Token(8), kind = "info", dir = "in", from = n.name, status = "done", note = "已连接 " + n.name + "，以后会自动连上" });
      app.Changes.Bump(); return;
     case "rejected": n.state = "rejected"; app.Changes.Bump(); return;
     default: n.state = "failed"; n.error = "对方没有回应，请重试"; app.Changes.Bump(); return;
    }
   }
   if (n.state == "waiting") { n.state = "failed"; n.error = "对方两分钟没有点允许"; }
  } catch (Exception e) {
   n.state = "failed"; n.error = e is ApiError ? e.Message : viaRelay ? "连不上中转服务器，请检查网络" : "连不上这台电脑。请确认它开着轻桥并允许了防火墙";
   Log.Write("配对失败：" + e);
  }
  app.Changes.Bump();
 }

 /// <summary>改用某个中转地址（连接码里带的）：记成最新的，组里其他电脑跟着换。</summary>
 void UseRelay(string url) {
  bool tempOff;
  lock (Gate) { tempOff = C.relayTemp && url != app.Temp.Url; if (tempOff) C.relayTemp = false; C.relay = url; C.relayAt = Util.Ms(); }
  if (tempOff) app.Temp.Stop();
  app.Save(); app.Changes.Bump();
  Util.Thread("relay-announce", () => { AnnounceRelay(); app.Relay.Kick(); foreach (var l in Links()) { l.LastHello = 0; l.Signal.Set(); } });
 }

 /// <summary>加入对方的设备组；原来组里在线的电脑一起带过去。</summary>
 void Adopt(Dictionary<string, object> p) {
  string newId = p.Get("familyId"), newKey = p.Get("familyKey");
  if (newKey.Length == 0) throw new ApiError(400, "配对数据不完整");
  var members = ParseMembers(p);
  AdoptRelay(p);
  List<Link> old; lock (Gate) old = links.Values.Where(l => l.PushOk).ToList();
  foreach (var l in old) {
   try { Call(l.Url, "POST", "/lb/migrate", new { familyId = newId, familyKey = newKey, members = p["members"] }, 4000); } catch { }
  }
  SwitchFamily(newId, newKey, members);
 }

 static List<Member> ParseMembers(Dictionary<string, object> p) {
  var list = new List<Member>();
  if (p.TryGetValue("members", out var raw) && raw is IEnumerable e)
   foreach (var x in e) if (x is Dictionary<string, object> d) {
    var m = new Member { id = d.Get("id"), name = Util.Clip(d.Get("name"), 40), form = d.Get("form"), port = (int)d.Num("port", 49321), added = Util.Ms() };
    m.addrs = d.List("addrs").Where(a => IPAddress.TryParse(a, out var ip) && Util.IsPrivate(ip)).Take(6).ToList();
    if (m.id.Length > 0 && m.id.Length <= 40) list.Add(m);
   }
  return list;
 }

 void SwitchFamily(string familyId, string familyKey, List<Member> members) {
  var toStart = new List<Link>();
  lock (Gate) {
   C.familyId = familyId; C.familyKey = familyKey;
   keys = new Keys(Convert.FromBase64String(familyKey));
   foreach (var m in members) {
    if (m.id == C.deviceId) continue;
    C.blocked.Remove(m.id);
    var have = C.members.FirstOrDefault(x => x.id == m.id);
    if (have == null) { C.members.Add(m); have = m; } else { have.name = m.name; foreach (var a in m.addrs) if (!have.addrs.Contains(a)) have.addrs.Add(a); have.port = m.port; }
    if (!links.ContainsKey(m.id)) toStart.Add(StartLink(have));
   }
   app.Save();
   foreach (var l in links.Values) { l.Error = null; l.Fails = 0; l.Signal.Set(); }
  }
  app.Discovery.Kick();
  app.Changes.Bump();
 }

 // ───────────── 别人来连我 ─────────────

 public object JoinIn(Dictionary<string, object> d, Req q) {
  string id = d.Get("id");
  if (id.Length == 0 || id.Length > 40 || id == C.deviceId) throw new ApiError(400, "请求无效");
  if (d.Get("tag") == Tag) return new { status = "member" };
  if (C.paused) throw new ApiError(503, "对方暂停了轻桥");
  byte[] shared; string pub;
  using (var hs = new Handshake()) { shared = hs.Derive(d.Get("pub")); pub = hs.Public; }
  var r = new JoinReq {
   id = Crypto.Token(12), kind = "pc", peerId = id, name = Util.Clip(d.Get("name"), 40), form = d.Get("form"), ip = q.Via != null ? null : q.Remote.ToString(),
   port = (int)d.Num("port", 49321), code = Handshake.Code(shared), shared = shared, until = Util.Ms() + 120000
  };
  lock (Gate) {
   Requests.RemoveAll(x => x.until < Util.Ms() || (x.kind == "pc" && x.peerId == id));
   if (Requests.Count(x => x.kind == "pc") >= 5) throw new ApiError(429, "请求太多，请稍后再试");
   Requests.Add(r);
  }
  app.Changes.Bump();
  app.Attention("“" + r.name + "”想连接这台电脑", "打开轻桥点“允许”");
  return new { status = "pending", req = r.id, pub, name = C.deviceName };
 }

 public object JoinPoll(Dictionary<string, object> d) {
  lock (Gate) {
   var r = Requests.FirstOrDefault(x => x.id == d.Get("req") && x.kind == "pc");
   if (r == null || (r.until < Util.Ms() && r.status == "pending")) return new { status = "expired" };
   if (r.status == "pending") return new { status = "pending" };
   Requests.Remove(r);
   if (r.status == "rejected") return new { status = "rejected" };
   return new { status = "ok", data = Convert.ToBase64String(r.payload) };
  }
 }

 public void Answer(string reqId, bool allow) {
  JoinReq r; lock (Gate) r = Requests.FirstOrDefault(x => x.id == reqId);
  if (r == null) throw new ApiError(404, "这个请求已经过期了");
  if (r.kind == "phone") { app.Phones.Answer(r, allow); return; }
  if (!allow) { r.status = "rejected"; r.until = Util.Ms() + 30000; app.Changes.Bump(); return; }
  object payload;
  lock (Gate) {
   C.blocked.Remove(r.peerId);
   var list = C.members.Where(m => !C.blocked.Contains(m.id) && m.id != r.peerId).Select(m => new Dictionary<string, object> { ["id"] = m.id, ["name"] = m.name, ["port"] = m.port, ["addrs"] = m.addrs.ToArray(), ["form"] = m.form ?? "" }).ToList();
   list.Add(new Dictionary<string, object> { ["id"] = C.deviceId, ["name"] = C.deviceName, ["port"] = app.Opt.Port, ["addrs"] = app.Opt.Loopback ? new[] { "127.0.0.1" } : Util.LocalAddrs().Select(a => a.Ip.ToString()).Take(6).ToArray(), ["form"] = Shell.Form });
   payload = new { familyId = C.familyId, familyKey = C.familyKey, members = list, relay = C.relay, relayAt = C.relayAt };
  }
  r.payload = new Keys(r.shared).Seal(J.Bytes(payload)); r.status = "ok"; r.until = Util.Ms() + 60000;
  var l = EnsureLink(r.peerId, r.name, r.form);
  if (l != null) { lock (Gate) { AddAddr(l.M, r.ip, r.port); l.M.name = r.name; } app.Save(); l.Signal.Set(); }
  lock (Gate) Near.Remove(r.peerId);
  app.Changes.Bump();
 }

 // ───────────── 组内消息 ─────────────

 public object MigrateIn(Link l, Dictionary<string, object> d) {
  string id = d.Get("familyId"), key = d.Get("familyKey");
  var members = ParseMembers(d);
  if (key.Length == 0) throw new ApiError(400, "缺少密钥");
  // 先回复（用旧密钥加密），再切换。
  Util.Thread("migrate", () => { Thread.Sleep(300); SwitchFamily(id, key, members); });
  return new { ok = true };
 }

 /// <summary>把一台电脑移出设备组：告诉组里其他电脑，也告诉它本人。</summary>
 public void Remove(string id) {
  Link l; List<Link> others;
  lock (Gate) { links.TryGetValue(id ?? "", out l); others = links.Values.Where(x => x.Id != id && x.PushOk).ToList(); }
  if (l == null) throw new ApiError(404, "没有这台设备");
  foreach (var o in others) try { Call(o.Url, "POST", "/lb/block", new { id }, 3000); } catch { }
  if (l.PushOk) try { Call(l.Url, "POST", "/lb/leave", new { }, 3000); } catch { }
  Block(id);
 }

 void Block(string id) {
  lock (Gate) {
   if (!C.blocked.Contains(id)) C.blocked.Add(id);
   C.members.RemoveAll(m => m.id == id);
   if (links.TryGetValue(id, out var l)) { l.Stopped = true; l.Signal.Set(); l.WaitSignal.Set(); links.Remove(id); }
   app.Save();
  }
  app.Changes.Bump();
 }

 public object BlockIn(Link from, Dictionary<string, object> d) {
  string id = d.Get("id");
  if (id == C.deviceId) Util.Thread("leave", () => { Thread.Sleep(300); Leave(); });
  else if (id.Length > 0) Block(id);
  return new { ok = true };
 }

 public object LeaveIn(Link from) { Util.Thread("leave", () => { Thread.Sleep(300); Leave(); }); return new { ok = true }; }

 /// <summary>被移出设备组：换一把新密钥，自己单独成组。</summary>
 void Leave() {
  lock (Gate) {
   foreach (var l in links.Values) { l.Stopped = true; l.Signal.Set(); l.WaitSignal.Set(); }
   links.Clear();
   app.Store.NewFamily(); keys = new Keys(Convert.FromBase64String(C.familyKey));
   app.Save();
  }
  app.Feed.Add(new FeedItem { id = Crypto.Token(8), kind = "info", dir = "in", status = "done", note = "这台电脑被移出了设备组" });
  app.Changes.Bump();
 }
}
