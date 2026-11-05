using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace LanBridge;

/// <summary>
/// 小云：常驻中转上的小文件池（FilePool）。组里的电脑都能看到、下载；每个文件还有一个链接，手机或别人在浏览器里打开就能下载。
/// 文件在这台电脑上就加密：每个文件一把随机密钥（和发文件同一种加密），文件名放在密文里；
/// 密钥和文件名再用设备组密钥加密成 meta 存在服务器上，只有同组的电脑解得开。链接把密钥放在 # 后面，服务器拿不到。
/// </summary>
sealed class Cloud {
 readonly App app; Config C => app.C;
 public Cloud(App app) { this.app = app; }

 public bool Ready => app.Relay.Ready && app.Relay.PoolMB > 0;
 byte[] FamilyKey => Convert.FromBase64String(C.familyKey);
 string Space => Crypto.Hex(Crypto.Hmac(FamilyKey, "lanbridge/2/pool-id")).Substring(0, 16);
 string Secret => Crypto.Hex(Crypto.Hmac(FamilyKey, "lanbridge/2/pool-auth"));
 Keys Group => new Keys(FamilyKey);

 public sealed class Item { public string id, name, from, link; public long size, at, stored; }
 List<Item> cache = new List<Item>(); long usedMB, capMB, maxFileMB; long rev; string error;
 readonly Dictionary<string, string> keys = new Dictionary<string, string>();

 HttpWebRequest Make(string path, string method, int timeout) {
  string b = app.Relay.Base ?? throw new ApiError(400, "还没开跨网络");
  var r = Relay.Make(b + path, method, timeout); r.Headers["X-LB-Pool"] = Secret; return r;
 }
 static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

 void Need() {
  if (app.Relay.Base == null) throw new ApiError(400, "先开启跨网络（常驻中转），小云放在中转服务器上");
  if (!app.Relay.Ready) throw new ApiError(503, "中转暂时连不上，连上后再试");
  if (app.Relay.PoolMB <= 0) throw new ApiError(400, "现在用的中转没有小云（临时中转和旧版中转不带文件池）");
 }

 /// <summary>列出组里放在小云上的文件（新的在前）。</summary>
 public object List() {
  if (!Ready) return new { ready = false, reason = app.Relay.Base == null ? "off" : !app.Relay.Ready ? "down" : "nopool", files = new object[0], capMB = 0, usedMB = 0 };
  try {
   var d = J.Obj(Client.Send(Make("/f/" + Space + "/list", "GET", 15000), null));
   var g = Group; var list = new List<Item>();
   if (d.TryGetValue("files", out var raw) && raw is System.Collections.IEnumerable e)
    foreach (var x in e) if (x is Dictionary<string, object> f) {
     try {
      var m = J.Obj(g.Open(Convert.FromBase64String(f.Get("meta"))));
      string id = f.Get("id"), key = m.Get("key");
      lock (keys) keys[id] = key;
      list.Add(new Item { id = id, name = m.Get("name"), from = m.Get("from"), size = m.Num("size"), stored = f.Num("size"), at = f.Num("at"), link = app.Relay.Base + "/d/" + id + "#" + key });
     } catch { } // 别的设备组或旧密钥放的：看不到
    }
   lock (keys) { cache = list; usedMB = (long)Math.Ceiling(Convert.ToDouble(d.TryGetValue("usedMB", out var u) ? u : 0)); capMB = d.Num("capMB"); maxFileMB = d.Num("maxFileMB"); error = null; }
  } catch (Exception ex) { lock (keys) error = ex is ApiError ? ex.Message : "读不到小云：" + ex.Message; }
  lock (keys) return new { ready = true, files = cache, capMB, usedMB, maxFileMB, error };
 }

 /// <summary>放到小云：在后台加密上传，进度显示在“动态”里。文件夹先打包成 zip。</summary>
 public void Upload(string[] paths) {
  Need();
  if (paths == null || paths.Length == 0) throw new ApiError(400, "没有选文件");
  foreach (var p in paths) if (!File.Exists(p) && !Directory.Exists(p)) throw new ApiError(404, "找不到 " + p);
  Util.Thread("cloud-up", () => {
   foreach (var p in paths) {
    string path = p, temp = null;
    if (Directory.Exists(p)) { try { temp = path = Shell.ZipFolder(p, Path.Combine(app.Root, "stage")); } catch (Exception e) { Fail(Path.GetFileName(p), e.Message); continue; } }
    try { Put(path, Path.GetFileName(path)); }
    finally { if (temp != null) try { File.Delete(temp); } catch { } }
   }
  });
 }

 void Fail(string name, string why) => app.Feed.Add(new FeedItem { id = Crypto.Token(8), kind = "send", dir = "out", status = "failed", to = new List<string> { "小云" }, note = why, files = new List<FeedFile> { new FeedFile { name = name } } });

 void Put(string path, string name) {
  var fi = new FileInfo(path);
  var nameBytes = Encoding.UTF8.GetBytes(name);
  long plain = 4 + nameBytes.Length + fi.Length, sealedLen = Keys.SealedLength(plain);
  var item = app.Feed.Add(new FeedItem { id = Crypto.Token(8), kind = "send", dir = "out", status = "sending", to = new List<string> { "小云" }, total = fi.Length, files = new List<FeedFile> { new FeedFile { name = name, path = path, size = fi.Length } } });
  try {
   long maxMB = app.Relay.PoolMB > 0 ? Math.Min(app.Relay.PoolMB, app.Relay.MaxBody >> 20) : 0;
   if (maxFileMB > 0) maxMB = Math.Min(maxMB, maxFileMB);
   if (maxMB > 0 && sealedLen > maxMB << 20) throw new ApiError(413, "小云一次最多放 " + maxMB + " MB");
   var key = Crypto.Random(32); var k = new Keys(key);
   string meta = Convert.ToBase64String(Group.Seal(J.Bytes(new { v = 1, name, size = fi.Length, from = C.deviceName, key = B64Url(key) })));
   var r = Make("/f/" + Space, "POST", 60000);
   r.ContentType = "application/octet-stream"; r.ContentLength = sealedLen; r.AllowWriteStreamBuffering = false; r.ReadWriteTimeout = 120000; r.Timeout = 600000;
   r.Headers["X-LB-Meta"] = meta;
   var head = new byte[4 + nameBytes.Length]; BitConverter.GetBytes(nameBytes.Length).CopyTo(head, 0); nameBytes.CopyTo(head, 4);
   long last = 0;
   using (var s = r.GetRequestStream())
   using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16)) {
    var input = new Concat(new MemoryStream(head), f);
    k.SealStream(input, plain, s, done => { if (done - last > 256 * 1024 || done == plain) { last = done; app.Feed.Update(item.id, x => x.done = Math.Max(0, done - head.Length), false); } });
   }
   var d = J.Obj(Client.ReadBody((HttpWebResponse)r.GetResponse()));
   lock (keys) keys[d.Get("id")] = B64Url(key);
   app.Feed.Update(item.id, x => { x.status = "done"; x.done = x.total; x.note = "已放到小云，组里的电脑都能下载"; });
   rev++; app.Changes.Bump();
  } catch (Exception e) {
   string why = e is ApiError ? e.Message : e is WebException we && we.Response is HttpWebResponse er ? ErrorOf(er) : "上传没成功：" + e.Message;
   app.Feed.Update(item.id, x => { x.status = "failed"; x.note = why; });
   Log.Write("小云上传失败：" + e.Message);
  }
 }

 static string ErrorOf(HttpWebResponse er) { try { using (er) return J.Obj(Client.ReadBody(er)).Get("error"); } catch { return "上传没成功（" + (int)er.StatusCode + "）"; } }

 /// <summary>下载到“收到的文件”：在后台下，完成后出现在动态里，可以打开。</summary>
 public void Download(string id) {
  Need();
  string key; lock (keys) keys.TryGetValue(id ?? "", out key);
  if (key == null) { List(); lock (keys) keys.TryGetValue(id ?? "", out key); }
  if (key == null) throw new ApiError(404, "这个文件已经不在小云上了");
  Item info; lock (keys) info = cache.FirstOrDefault(x => x.id == id);
  var item = app.Feed.Add(new FeedItem { id = Crypto.Token(8), kind = "send", dir = "in", from = "小云", status = "receiving", total = info?.size ?? 0, files = new List<FeedFile> { new FeedFile { name = info?.name ?? "文件", size = info?.size ?? 0 } } });
  Util.Thread("cloud-down", () => {
   string part = null;
   try {
    var r = Relay.Make(app.Relay.Base + "/f/" + id, "GET", 60000); r.ReadWriteTimeout = 120000;
    using var resp = (HttpWebResponse)r.GetResponse();
    using var s = resp.GetResponseStream();
    Directory.CreateDirectory(C.receiveFolder);
    var split = new NameSplit(C.receiveFolder);
    var raw = Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/') + new string('=', (4 - key.Length % 4) % 4));
    try { new Keys(raw).OpenStream(s, resp.ContentLength, split, done => app.Feed.Update(item.id, x => x.done = Math.Max(0, done - 64), false)); }
    finally { part = split.Part; split.Close(); }
    string final = Util.Reserve(C.receiveFolder, Util.SafeName(split.Name ?? "小云文件"));
    if (File.Exists(final)) File.Delete(final);
    File.Move(part, final); Util.Release(final); part = null;
    long size = new FileInfo(final).Length;
    app.Feed.Update(item.id, x => { x.status = "done"; x.done = x.total = size; x.files = new List<FeedFile> { new FeedFile { name = Path.GetFileName(final), path = final, size = size } }; x.note = null; });
    if (C.notify) app.Notify("已从小云下载", Path.GetFileName(final), () => Shell.Reveal(final));
   } catch (Exception e) {
    string why = e is ApiError ? e.Message : e is WebException we && we.Response is HttpWebResponse er && er.StatusCode == HttpStatusCode.NotFound ? "这个文件已经不在小云上了（满了会先删最早的）" : "下载没成功：" + e.Message;
    app.Feed.Update(item.id, x => { x.status = "failed"; x.note = why; });
    Log.Write("小云下载失败：" + e.Message);
   } finally { if (part != null) try { File.Delete(part); } catch { } }
  });
 }

 public void Delete(string id) {
  Need();
  Client.Send(Make("/f/" + Space + "/" + id + "/delete", "POST", 15000), new byte[0]);
  lock (keys) { cache.RemoveAll(x => x.id == id); keys.Remove(id); }
  rev++; app.Changes.Bump();
 }
 public long Rev => rev;

 /// <summary>把两个流接成一个（文件名头 + 文件内容）。</summary>
 sealed class Concat : Stream {
  readonly Stream a, b; bool first = true;
  public Concat(Stream a, Stream b) { this.a = a; this.b = b; }
  public override int Read(byte[] buf, int o, int n) { if (first) { int r = a.Read(buf, o, n); if (r > 0) return r; first = false; } return b.Read(buf, o, n); }
  public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
  public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
  public override void Flush() { }
  public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
  public override void Write(byte[] x, int o, int n) => throw new NotSupportedException();
 }

 /// <summary>解密后的数据：先是 4 字节长度 + 文件名，之后才是文件内容，写到一个临时文件里。</summary>
 sealed class NameSplit : Stream {
  readonly string dir; readonly MemoryStream head = new MemoryStream(); int need = -1; FileStream file;
  public string Name, Part;
  public NameSplit(string dir) { this.dir = dir; }
  public override void Write(byte[] buf, int o, int n) {
   while (n > 0 && file == null) {
    int want = need < 0 ? 4 - (int)head.Length : need - (int)head.Length;
    int take = Math.Min(want, n); head.Write(buf, o, take); o += take; n -= take;
    if (need < 0 && head.Length == 4) { need = BitConverter.ToInt32(head.ToArray(), 0); if (need < 0 || need > 4096) throw new ApiError(400, "文件格式不对"); head.SetLength(0); }
    if (need >= 0 && head.Length == need) {
     Name = Path.GetFileName(Encoding.UTF8.GetString(head.ToArray()));
     Part = Path.Combine(dir, "." + Crypto.Token(6) + ".lbpart");
     file = new FileStream(Part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
    }
   }
   if (n > 0) file.Write(buf, o, n);
  }
  public override void Close() { file?.Dispose(); file = null; base.Close(); }
  public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
  public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
  public override void Flush() { file?.Flush(); }
  public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
  public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
 }
}
