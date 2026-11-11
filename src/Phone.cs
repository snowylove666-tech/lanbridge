using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LanBridge;

sealed class InboxItem { public string id, phoneId, name, path; public long size, time; public bool got; }

/// <summary>
/// 手机这一侧：iPhone 用 Safari 打开电脑地址，或用“快捷指令”轻点背面同步文字。不用装 App。
/// 配对：电脑上显示二维码，相机一扫就连上（二维码里带一次性密钥，扫码即代表人在电脑跟前）。
/// </summary>
sealed class PhoneHub {
 readonly App app; Config C => app.C; object Gate => app.Gate;
 public string PairSecret; public long PairUntil;
 readonly Changes changes = new Changes();
 readonly List<InboxItem> inbox = new List<InboxItem>();
 readonly Dictionary<string, (string text, string clip, long until)> receipts = new Dictionary<string, (string, string, long)>();
 readonly Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();

 public PhoneHub(App app) { this.app = app; RefreshPair(); MergeOld(); }

 void MergeOld() {
  if (C.phonesMergeVer >= 2) return;
  var moves = new List<(List<string> from, string to)>();
  lock (Gate) {
   foreach (var g in C.phones.GroupBy(x => x.name).Where(g => g.Count() > 1).ToList()) {
    var keep = g.OrderByDescending(x => x.seen).First();
    var drop = g.Where(x => x != keep).ToList();
    foreach (var o in drop) Absorb(keep, o);   // 并掉的卡片的授权也留着：那些地方打开的网页不用重新扫码
    var ids = drop.Select(x => x.id).ToList();
    C.phones.RemoveAll(x => ids.Contains(x.id));
    moves.Add((ids, keep.id));
   }
   C.phonesMerged = true; C.phonesMergeVer = 2; app.Save();
  }
  foreach (var m in moves) app.History.Merge(m.from, m.to);
 }

 public void RefreshPair() { lock (Gate) { PairSecret = Crypto.Token(10); PairUntil = Util.Ms() + 15 * 60 * 1000; } app.Changes.Bump(); }

 public void ClipChanged() { try { app.ClipLog.Add(app.Hub.Latest, app.Hub.LatestPaths); } catch { } changes.Bump(); }

 public object HistoryClear(Phone p) { lock (Gate) { p.histFrom = Util.Ms(); app.Save(); } return new { ok = true }; }
 public object HistoryDays(Phone p, Dictionary<string, object> d) {
  int n = (int)d.Num("days", 7); if (n != 0 && n != 1 && n != 7 && n != 30) n = 7;
  lock (Gate) { p.histDays = n; app.Save(); } return new { ok = true, days = n };
 }

 Phone Add(string name, out string token) {
  token = Crypto.Token(24);
  string nm = Util.Clip(string.IsNullOrWhiteSpace(name) ? "我的 iPhone" : name.Trim(), 30);
  Phone p; List<string> merged;
  lock (Gate) {
   // 同名的就是同一部手机：它从 Safari、主屏幕图标、微信、家里地址、中转地址打开时各拿一份授权，
   // 都记在同一张卡片上（以前每次算一部新手机，设备越扫越多）。真有两部同名的，在手机连接页上改个名字。
   long now = Util.Ms();
   var same = C.phones.Where(x => x.name == nm).OrderByDescending(x => x.seen).ToList();
   p = same.FirstOrDefault();
   merged = same.Skip(1).Select(x => x.id).ToList();
   if (p != null) {
    foreach (var o in same.Skip(1)) Absorb(p, o);
    C.phones.RemoveAll(x => merged.Contains(x.id));
    foreach (var x in inbox) if (merged.Contains(x.phoneId)) x.phoneId = p.id;
    p.hashes ??= new List<string>(); p.hashes.Add(Crypto.Sha(token));
    while (p.hashes.Count > 8) p.hashes.RemoveAt(0);
    p.seen = now;
   } else {
    p = new Phone { id = Crypto.Token(6), name = nm, hash = Crypto.Sha(token), added = now, seen = now };
    C.phones.Add(p); while (C.phones.Count > 20) C.phones.RemoveAt(0);
   }
   app.Save();
  }
  if (merged.Count > 0) app.History.Merge(merged, p.id);
  app.Changes.Bump();
  return p;
 }
 static void Absorb(Phone keep, Phone o) {
  keep.hashes ??= new List<string>();
  if (o.hash != null) keep.hashes.Add(o.hash);
  if (o.hashes != null) keep.hashes.AddRange(o.hashes);
  keep.seen = Math.Max(keep.seen, o.seen);
 }

 public object PairIn(Dictionary<string, object> d) {
  string secret = d.Get("secret");
  lock (Gate) if (secret.Length == 0 || secret != PairSecret || Util.Ms() > PairUntil) throw new ApiError(403, "二维码已过期。请在电脑上的轻桥重新打开“连接手机”再扫一次");
  var p = Add(d.Get("name"), out var token);
  RefreshPair();
  app.Feed.Add(new FeedItem { id = Crypto.Token(8), kind = "info", dir = "in", from = p.name, status = "done", note = p.name + " 已连接这台电脑" });
  return new { token, id = p.id, name = p.name, pc = C.deviceName };
 }

 public object JoinIn(Dictionary<string, object> d, Req q) {
  if (C.paused) throw new ApiError(503, "电脑上的轻桥暂停了");
  var r = new JoinReq { id = Crypto.Token(12), kind = "phone", name = Util.Clip(string.IsNullOrWhiteSpace(d.Get("name")) ? "我的 iPhone" : d.Get("name"), 30), ip = q.Via != null ? null : q.Remote.ToString(), code = (BitConverter.ToUInt32(Crypto.Random(4), 0) % 10000).ToString("D4"), until = Util.Ms() + 120000 };
  lock (Gate) {
   app.Hub.Requests.RemoveAll(x => x.until < Util.Ms());
   if (app.Hub.Requests.Count(x => x.kind == "phone") >= 5) throw new ApiError(429, "请求太多，请稍后再试");
   app.Hub.Requests.Add(r);
  }
  app.Changes.Bump();
  app.Attention("“" + r.name + "”想连接这台电脑", "核对码 " + r.code + "，打开轻桥点“允许”");
  return new { req = r.id, code = r.code };
 }

 public object JoinPoll(Dictionary<string, object> d) {
  lock (Gate) {
   var r = app.Hub.Requests.FirstOrDefault(x => x.id == d.Get("req") && x.kind == "phone");
   if (r == null || (r.until < Util.Ms() && r.status == "pending")) return new { status = "expired" };
   if (r.status == "pending") return new { status = "pending" };
   app.Hub.Requests.Remove(r);
   if (r.status == "rejected") return new { status = "rejected" };
   return new { status = "ok", token = r.token, pc = C.deviceName };
  }
 }

 public void Answer(JoinReq r, bool allow) {
  if (allow) { Add(r.name, out var token); r.token = token; r.status = "ok"; } else r.status = "rejected";
  r.until = Util.Ms() + 60000; app.Changes.Bump();
 }

 public void Remove(string id) { lock (Gate) { C.phones.RemoveAll(p => p.id == id); inbox.RemoveAll(x => x.phoneId == id); app.Save(); } app.Changes.Bump(); }

 public Phone Auth(Req q) {
  string t = q.H("Authorization"); if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(7).Trim(); else t = q.H("X-LB-Token");
  if (t.Length == 0) t = q.Q("t");
  if (t.Length < 16) throw new ApiError(401, "这部手机还没连接电脑");
  string h = Crypto.Sha(t);
  Phone p; bool bump;
  lock (Gate) {
   p = C.phones.FirstOrDefault(x => x.hash == h || (x.hashes != null && x.hashes.Contains(h))) ?? throw new ApiError(401, "这部手机已被电脑移除，请重新扫码连接");
   bump = Util.Ms() - p.seen > 20000; p.seen = Util.Ms();
  }
  if (bump) app.Changes.Bump();
  return p;
 }

 // ───────────── 手机网页 ─────────────

 public object State(Phone p, long since) {
  long rev = since > 0 ? changes.Wait(since, 25000) : changes.Rev;
  lock (Gate) p.seen = Util.Ms();
  return new { rev, pc = C.deviceName, paused = C.paused, me = p.name, mdns = app.MdnsName, port = app.Opt.Port, relay = app.Relay.Ready ? app.Relay.PhoneUrl : null, clip = ClipView(p), auto = p.swapAt, hist = app.ClipLog.Rev, inbox = Inbox(p), cloud = app.Cloud.Ready ? app.Cloud.Rev : -1 };
 }

 object ClipView(Phone p) {
  var ev = app.Hub.Latest; var paths = app.Hub.LatestPaths;
  if (ev == null) return null;
  bool fromMe = ev.via != null && ev.via == p.name;
  if (ev.kind == "text") return new { ev.id, kind = "text", text = ev.text, from = ev.via ?? ev.originName, time = ev.stamp, fromMe };
  return new { ev.id, kind = ev.kind, from = ev.via ?? ev.originName, time = ev.stamp, fromMe, items = ev.items.Select((x, i) => new { x.name, x.size, ok = paths != null && i < paths.Length && File.Exists(paths[i]) }).ToList() };
 }

 object Inbox(Phone p) { lock (Gate) return inbox.Where(x => x.phoneId == p.id).OrderByDescending(x => x.time).Take(50).Select(x => new { x.id, x.name, x.size, x.time, x.got }).ToList(); }

 public object TextIn(Phone p, Dictionary<string, object> d) {
  string text = d.Get("text");
  if (text.Length == 0) throw new ApiError(400, "没有文字");
  if (text.Length > 4 * 1024 * 1024) throw new ApiError(413, "文字太长");
  if (C.paused) throw new ApiError(503, "电脑上的轻桥暂停了");
  if (!app.Clip.WriteText(text)) throw new ApiError(409, "电脑剪贴板正被别的程序占用，请再试一次");
  app.Hub.LocalCopy("text", text, null, p.name);
  lock (Gate) { p.baseline = Crypto.Sha(text); p.init = true; p.lastClip = app.Hub.Latest?.id; app.Save(); }
  app.History.Add(p, "text", text, null);
  if (C.notify) app.Notify("收到 " + p.name + " 的文字", Util.Clip(text.Replace("\r", " ").Replace("\n", " "), 60) + "\n已放进剪贴板，直接 Ctrl+V", app.ShowWindow);
  return new { ok = true, message = "已放进电脑剪贴板" };
 }

 /// <summary>手机上传文件。clip=1 时上传完放进电脑剪贴板（一张图就是图片，多个就是文件）。</summary>
 public object UploadIn(Phone p, Req q) {
  if (C.paused) throw new ApiError(503, "电脑上的轻桥暂停了");
  string name = Util.SafeName(q.Q("name")); long size = q.Length;
  if (size <= 0) throw new ApiError(400, "文件是空的");
  if (size > 8L << 30) throw new ApiError(413, "单个文件最大 8 GB");
  bool cloud = q.Q("to") == "cloud";
  if (cloud) app.Cloud.CheckRoom(size);
  string folder = cloud ? Path.Combine(app.Root, "stage", "cloud-" + Crypto.Token(6)) : C.receiveFolder;
  Directory.CreateDirectory(folder);
  string path = Util.Reserve(folder, name), part = path + ".lbpart";
  try {
   using (var f = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) {
    var buf = new byte[1 << 16]; long done = 0; int n;
    while (done < size && (n = q.Body.Read(buf, 0, (int)Math.Min(buf.Length, size - done))) > 0) { f.Write(buf, 0, n); done += n; }
    if (done != size) throw new ApiError(400, "上传中断了");
   }
   File.Move(part, path);
  } catch { try { File.Delete(part); } catch { } throw; }
  finally { Util.Release(path); }
  if (cloud) { app.Cloud.UploadFrom(path, p.name); return new { ok = true, name = Path.GetFileName(path), complete = true }; }

  string group = q.Q("group"); int.TryParse(q.Q("count"), out int count); bool clip = q.Q("clip") == "1";
  List<string> done2 = null;
  lock (Gate) {
   if (group.Length > 0 && count > 1) {
    if (!groups.TryGetValue(group, out var list)) groups[group] = list = new List<string>();
    list.Add(path);
    if (list.Count >= count) { groups.Remove(group); done2 = list; }
   } else done2 = new List<string> { path };
  }
  if (done2 != null) Deliver(p, done2, clip);
  return new { ok = true, name = Path.GetFileName(path), complete = done2 != null };
 }

 void Deliver(Phone p, List<string> paths, bool clip) {
  string[] imageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff" };
  bool oneImage = paths.Count == 1 && imageExt.Contains(Path.GetExtension(paths[0]).ToLowerInvariant()) && Path.GetExtension(paths[0]).ToLowerInvariant() != ".heic";
  app.History.Add(p, oneImage ? "image" : "files", null, paths);
  if (clip) {
   bool ok = oneImage ? app.Clip.WriteImage(paths[0]) : app.Clip.WriteFiles(paths.ToArray());
   if (ok) {
    app.Hub.LocalCopy(oneImage ? "image" : "files", null, paths.ToArray(), p.name);
    if (C.notify) app.Notify("收到 " + p.name + " 的" + (oneImage ? "照片" : paths.Count + " 个文件"), "已放进剪贴板，直接 Ctrl+V", app.ShowWindow);
    return;
   }
  }
  app.Feed.Add(new FeedItem { id = Crypto.Token(12), kind = "send", dir = "in", from = p.name, to = new List<string> { C.deviceName }, status = "done", total = paths.Sum(x => new FileInfo(x).Length), image = oneImage, files = paths.Select(x => new FeedFile { name = Path.GetFileName(x), path = x, size = new FileInfo(x).Length }).ToList(), note = "已存到“" + Path.GetFileName(C.receiveFolder.TrimEnd('\\')) + "”文件夹" });
  if (C.notify) app.Notify("收到 " + (paths.Count == 1 ? Path.GetFileName(paths[0]) : paths.Count + " 个文件"), "来自 " + p.name + "，点这里打开", () => Shell.Reveal(paths[0]));
 }

 /// <summary>下载：剪贴板上的图片/文件，或电脑发给手机的文件。</summary>
 public void Download(Phone p, Req q, Res res) {
  string path, name;
  string box = q.Q("inbox");
  if (box.Length > 0) {
   InboxItem it; lock (Gate) it = inbox.FirstOrDefault(x => x.id == box && x.phoneId == p.id);
   if (it == null) throw new ApiError(404, "这个文件已经不在了");
   path = it.path; name = it.name; lock (Gate) it.got = true; changes.Bump(); app.Changes.Bump();
  } else if (q.Q("log").Length > 0) {
   var e = app.ClipLog.Find(q.Q("log")); int.TryParse(q.Q("i"), out int k);
   var lf = e?.files.ElementAtOrDefault(k);
   if (lf?.path == null) throw new ApiError(404, "这个文件已经不在了");
   path = lf.path; name = lf.name;
  } else {
   var ev = app.Hub.Latest; var paths = app.Hub.LatestPaths; int.TryParse(q.Q("i"), out int i);
   if (ev == null || ev.id != q.Q("ev") || paths == null || i < 0 || i >= paths.Length) throw new ApiError(404, "电脑剪贴板已经换了内容");
   path = paths[i]; name = ev.items[i].name;
  }
  var fi = new FileInfo(path); if (!fi.Exists) throw new ApiError(404, "文件已被移动或删除");
  string type = MimeOf(name);
  // 手机上看的是缩小后的图（经中转传原图又慢又占线，几张大图就把连接堵住了）；要原图另外点。
  if (int.TryParse(q.Q("thumb"), out int tw) && tw >= 64 && tw <= 2400 && fi.Length < 64 * 1024 * 1024 && (type == "image/png" || type == "image/jpeg" || type == "image/bmp" || type == "image/gif")) {
   byte[] small = null; try { small = ThumbOf(path, fi, tw); } catch { }
   if (small != null) { res.Bytes(small, "image/jpeg", 200, new Dictionary<string, string> { ["Content-Disposition"] = "inline" }); return; }
  }
  var headers = new Dictionary<string, string> { ["Content-Disposition"] = (q.Q("view") == "1" ? "inline" : "attachment") + "; filename*=UTF-8''" + Uri.EscapeDataString(name) };
  using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
  var s = res.Begin(200, type, fi.Length, headers); f.CopyTo(s, 1 << 16); s.Flush();
 }

 static readonly Dictionary<string, byte[]> thumbs = new Dictionary<string, byte[]>(); static readonly List<string> thumbKeys = new List<string>();
 static byte[] ThumbOf(string path, FileInfo fi, int max) {
  string key = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + max;
  lock (thumbs) if (thumbs.TryGetValue(key, out var hit)) return hit;
  byte[] data;
  using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
  using (var src = System.Drawing.Image.FromStream(f, false, false)) {
   // 手机拍的照片靠 EXIF 记方向：先转正
   try {
    if (Array.IndexOf(src.PropertyIdList, 0x0112) >= 0) {
     int o = src.GetPropertyItem(0x0112).Value[0];
     var flip = o == 3 ? System.Drawing.RotateFlipType.Rotate180FlipNone : o == 6 ? System.Drawing.RotateFlipType.Rotate90FlipNone : o == 8 ? System.Drawing.RotateFlipType.Rotate270FlipNone : System.Drawing.RotateFlipType.RotateNoneFlipNone;
     if (flip != System.Drawing.RotateFlipType.RotateNoneFlipNone) src.RotateFlip(flip);
    }
   } catch { }
   double k = Math.Min(1.0, (double)max / Math.Max(src.Width, src.Height));
   int w = Math.Max(1, (int)Math.Round(src.Width * k)), h = Math.Max(1, (int)Math.Round(src.Height * k));
   using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
   using (var g = System.Drawing.Graphics.FromImage(bmp)) {
    g.Clear(System.Drawing.Color.White);
    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    g.DrawImage(src, 0, 0, w, h);
   }
   var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/jpeg");
   using var ps = new System.Drawing.Imaging.EncoderParameters(1);
   ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
   using var m = new MemoryStream(); bmp.Save(m, enc, ps); data = m.ToArray();
  }
  if (data.Length < 700 * 1024) lock (thumbs) {
   if (!thumbs.ContainsKey(key)) { thumbs[key] = data; thumbKeys.Add(key); }
   while (thumbKeys.Count > 80) { thumbs.Remove(thumbKeys[0]); thumbKeys.RemoveAt(0); }
  }
  return data;
 }

 public static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch {
  ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".heic" => "image/heic", ".bmp" => "image/bmp",
  ".pdf" => "application/pdf", ".txt" => "text/plain; charset=utf-8", ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".mp3" => "audio/mpeg",
  ".zip" => "application/zip", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
  _ => "application/octet-stream"
 };

 /// <summary>电脑把文件“发给手机”：放进这部手机的收件箱，手机网页打开就能下载。</summary>
 public void Offer(string phoneId, string[] paths) {
  Phone p; lock (Gate) p = C.phones.FirstOrDefault(x => x.id == phoneId);
  if (p == null) throw new ApiError(404, "没有这部手机");
  var files = new List<string>();
  foreach (var x in paths) { if (Directory.Exists(x)) files.Add(Shell.ZipFolder(x, Path.Combine(app.Root, "stage"))); else if (File.Exists(x)) files.Add(Path.GetFullPath(x)); }
  if (files.Count == 0) throw new ApiError(400, "没有可发送的文件");
  lock (Gate) foreach (var f in files) inbox.Add(new InboxItem { id = Crypto.Token(8), phoneId = p.id, name = Path.GetFileName(f), path = f, size = new FileInfo(f).Length, time = Util.Ms() });
  changes.Bump();
  app.Feed.Add(new FeedItem { id = Crypto.Token(12), kind = "send", dir = "out", from = C.deviceName, to = new List<string> { p.name }, status = "waiting", total = files.Sum(x => new FileInfo(x).Length), files = files.Select(x => new FeedFile { name = Path.GetFileName(x), path = x, size = new FileInfo(x).Length }).ToList(), note = "已放进手机收件箱，手机打开轻桥网页就能下载" });
 }

 // ───────────── 快捷指令（轻点背面，兼容 1.5 的接口） ─────────────

 /// <summary>
 /// 一个快捷指令同时管两个方向：手机剪贴板变了就发给电脑；没变而电脑有新文字，就把电脑的文字带回手机。
 /// </summary>
 public object SyncIn(Phone p, Dictionary<string, object> d) {
  string text = d.Get("text"), mode = d.Get("mode"); if (mode.Length == 0) mode = "smart";
  if (C.paused) throw new ApiError(503, "电脑上的轻桥暂停了");
  lock (Gate) {
   if (!p.init && mode == "smart") {
    p.init = true; p.baseline = Crypto.Sha(text); p.lastClip = app.Hub.Latest?.id; app.Save();
    return Result("ready", "", "", "设置好了。以后在任意一边复制文字，再轻点背面就能同步。");
   }
  }
  var r = Decide(p, text, mode, false);
  return Result(r.action, r.action == "receive" ? r.text : "", r.receipt, r.message);
 }

 /// <summary>
 /// 判断这次轻点（或切 App 自动运行）该往哪边同步。
 /// 手机剪贴板比上次运行时变了 = 手机刚复制的，发到电脑（不会被电脑的覆盖）；
 /// 很久没运行过、分不清手机的是新是旧时：电脑 1 分钟内刚复制的优先。
 /// 电脑的文字 5 分钟内复制、手机还没拿过的，才放进手机剪贴板。
 /// </summary>
 (string action, string text, string receipt, string message) Decide(Phone p, string text, string mode, bool autoAck) {
  var latest = app.Hub.Latest;
  bool pcNew, pcRecent, fresh;
  string sha = Crypto.Sha(text);
  lock (Gate) {
   long now = Util.Ms();
   pcNew = latest?.kind == "text" && latest.id != p.lastClip && latest.via != p.name && now - latest.stamp < 5 * 60 * 1000;
   if (mode == "receive") pcNew = latest?.kind == "text";
   pcRecent = pcNew && now - latest.stamp < 60000;
   fresh = p.lastReportAt > 0 && now - p.lastReportAt < 10 * 60 * 1000 && sha != p.lastReport;
   if (text.Length > 0) { p.lastReport = sha; p.lastReportAt = now; }
  }
  bool phoneNew = mode != "receive" && text.Length > 0 && sha != p.baseline;
  if (phoneNew && pcNew && latest.text == text) { lock (Gate) { p.baseline = sha; p.lastClip = latest.id; app.Save(); } return ("noop", text, "", "两边已经一样了。"); }
  if (phoneNew && (fresh || !pcRecent)) {
   TextIn(p, new Dictionary<string, object> { ["text"] = text });
   return ("sent", text, "", "已发到电脑，可以直接粘贴。");
  }
  if (pcNew) {
   if (autoAck) { lock (Gate) { p.baseline = Crypto.Sha(latest.text); p.lastClip = latest.id; p.init = true; app.Save(); } return ("receive", latest.text, "", "已取回电脑上的文字。"); }
   string receipt = Crypto.Token(10);
   lock (Gate) { receipts[receipt] = (latest.text, latest.id, Util.Ms() + 120000); foreach (var k in receipts.Where(x => x.Value.until < Util.Ms()).Select(x => x.Key).ToList()) receipts.Remove(k); }
   return ("receive", latest.text, receipt, "已取回电脑上的文字。");
  }
  return ("noop", text, "", "没有新内容。");
 }

 /// <summary>
 /// 简化版快捷指令（只要 3 个动作：获取剪贴板 → 获取 URL 内容 → 拷贝至剪贴板）。
 /// 请求体就是手机剪贴板，回复就是手机剪贴板“应该变成”的内容：
 /// 手机有新内容 → 发给电脑并原样返回；电脑有新文字 → 返回电脑的文字；都没有 → 原样返回。
 /// </summary>
 public void SwapIn(Phone p, Req q, Res res) {
  if (C.paused) throw new ApiError(503, "电脑上的轻桥暂停了");
  lock (Gate) { bool first = p.swapAt == 0; p.swapAt = Util.Ms(); if (first) app.Save(); }
  changes.Bump(); app.Changes.Bump();   // 手机上的设置向导在等这一下：“电脑收到了”
  string type = q.H("Content-Type").ToLowerInvariant();
  if (type.StartsWith("image/")) {
   var bytes = q.ReadAll(64 * 1024 * 1024);
   string h = Crypto.Sha(bytes);
   bool same; lock (Gate) { same = p.imageHash == h; p.imageHash = h; }
   if (same) { res.Bytes(bytes, type.Split(';')[0]); return; }
   string ext = type.Contains("png") ? ".png" : type.Contains("jpeg") || type.Contains("jpg") ? ".jpg" : type.Contains("heic") ? ".heic" : type.Contains("gif") ? ".gif" : ".png";
   Directory.CreateDirectory(C.receiveFolder);
   string path = Util.Reserve(C.receiveFolder, p.name + " 图片 " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ext);
   try { File.WriteAllBytes(path, bytes); } finally { Util.Release(path); }
   Deliver(p, new List<string> { path }, ext != ".heic");
   res.Bytes(bytes, type.Split(';')[0]);
   return;
  }
  string text = q.Length > 0 ? new UTF8Encoding(false, false).GetString(q.ReadAll(4 * 1024 * 1024)) : "";
  lock (Gate) if (!p.init) { p.init = true; p.baseline = ""; }
  var r = Decide(p, text, "smart", true);
  res.Text(r.text);
 }

 public object AckIn(Phone p, Dictionary<string, object> d) {
  lock (Gate) {
   if (!receipts.TryGetValue(d.Get("receipt"), out var r)) return new { ok = true };
   receipts.Remove(d.Get("receipt"));
   p.baseline = Crypto.Sha(r.text); p.lastClip = r.clip; p.init = true; app.Save();
  }
  return new { ok = true };
 }

 static object Result(string action, string text, string receipt, string message) => new { ok = true, action, copy = action == "receive", text, receipt, message };
}
