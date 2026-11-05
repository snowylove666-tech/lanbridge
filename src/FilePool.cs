using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace LanBridge;

/// <summary>
/// 常驻中转上的小文件池（“小云”）：组里的电脑把文件放上来，在哪都能下载。
/// 总共不超过上限（默认 1 GB），放不下时从最早放上来的开始删。
/// 内容在电脑上就加密了（每个文件一把随机密钥），服务器只存密文；文件名、密钥也加密在 meta 里，只有同组的电脑解得开。
/// 链接 /d/文件ID#密钥：密钥在 # 后面，浏览器不会把它发给服务器，下载页在浏览器里解密。
/// </summary>
sealed class FilePool {
 readonly string dir; public readonly long Cap, MaxFile; readonly object gate = new object();
 public sealed class Entry { public string id, space, meta; public long size, at; }
 public sealed class Index { public List<Entry> files = new List<Entry>(); public Dictionary<string, string> spaces = new Dictionary<string, string>(); }
 Index ix; long reserved;

 public FilePool(string dir, long cap, long maxFile) {
  this.dir = dir; Cap = cap; MaxFile = Math.Min(cap, maxFile);
  Directory.CreateDirectory(dir);
  try { ix = J.Parse<Index>(File.ReadAllText(IndexPath, Encoding.UTF8)); } catch { ix = null; }
  ix ??= new Index(); ix.files ??= new List<Entry>(); ix.spaces ??= new Dictionary<string, string>();
  ix.files.RemoveAll(e => !File.Exists(Bin(e.id)));
  var known = new HashSet<string>(ix.files.Select(e => e.id));
  foreach (var f in Directory.GetFiles(dir)) {
   string n = Path.GetFileName(f);
   if (n.EndsWith(".part") || (n.EndsWith(".bin") && !known.Contains(n.Substring(0, n.Length - 4)))) try { File.Delete(f); } catch { }
  }
  Evict(0); Save();
 }
 string IndexPath => Path.Combine(dir, "index.json");
 string Bin(string id) => Path.Combine(dir, id + ".bin");
 public long Used { get { lock (gate) return ix.files.Sum(e => e.size); } }
 public int Count { get { lock (gate) return ix.files.Count; } }
 public object Info => new { capMB = Cap >> 20, usedMB = Math.Round(Used / 1048576.0, 1), files = Count, maxFileMB = MaxFile >> 20 };

 void Save() {
  lock (gate) {
   string tmp = IndexPath + ".tmp";
   File.WriteAllText(tmp, J.Str(ix), new UTF8Encoding(false));
   if (File.Exists(IndexPath)) File.Replace(tmp, IndexPath, null); else File.Move(tmp, IndexPath);
  }
 }

 /// <summary>腾出 need 字节：从最早的开始删。调用时已持有锁或在构造里。</summary>
 void Evict(long need) {
  lock (gate) {
   while (ix.files.Count > 0 && ix.files.Sum(e => e.size) + reserved + need > Cap) {
    var old = ix.files.OrderBy(e => e.at).First();
    ix.files.Remove(old);
    try { File.Delete(Bin(old.id)); } catch { }
    Log.Write("文件池满了，删掉最早的 " + old.id + "（" + (old.size >> 10) + " KB）");
   }
  }
 }

 static bool IsHex(string s, int n) => s.Length == n && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

 /// <summary>空间的口令：第一次用时记下哈希，之后只认这个。</summary>
 bool Auth(string space, Req q) {
  string secret = q.H("X-LB-Pool");
  if (secret.Length < 32 || secret.Length > 128) return false;
  string h = Crypto.Sha(Encoding.UTF8.GetBytes(secret));
  lock (gate) {
   if (ix.spaces.TryGetValue(space, out var have)) return Crypto.Same(Encoding.ASCII.GetBytes(have), Encoding.ASCII.GetBytes(h));
   ix.spaces[space] = h; Save(); return true;
  }
 }

 /// <summary>处理 /f/... 和 /d/...；不是这里的返回 false。</summary>
 public bool Handle(Req q, Res res, string[] parts) {
  string a = parts.Length > 1 ? parts[1] : "", b = parts.Length > 2 ? parts[2] : "", c = parts.Length > 3 ? parts[3] : "";
  if (a == "d" && IsHex(b, 32) && q.Method == "GET") { Page(res); return true; }
  if (a != "f") return false;
  if (IsHex(b, 32) && q.Method == "GET") { Download(res, b); return true; }
  if (!IsHex(b, 16)) { res.Json(new { error = "没有这个地址" }, 404); return true; }
  if (!Auth(b, q)) { res.Close = true; res.Json(new { error = "文件池口令不对" }, 401); return true; }
  if (c == "list" && q.Method == "GET") { List(res, b); return true; }
  if (c == "" && q.Method == "POST") { Upload(q, res, b); return true; }
  if (IsHex(c, 32) && (q.Method == "DELETE" || (q.Method == "POST" && parts.Length > 4 && parts[4] == "delete"))) { Delete(res, b, c); return true; }
  res.Json(new { error = "没有这个地址" }, 404); return true;
 }

 void List(Res res, string space) {
  List<object> files;
  lock (gate) files = ix.files.Where(e => e.space == space).OrderByDescending(e => e.at).Select(e => (object)new { e.id, e.size, e.at, e.meta }).ToList();
  res.Json(new { files, capMB = Cap >> 20, usedMB = Math.Round(Used / 1048576.0, 1), maxFileMB = MaxFile >> 20 });
 }

 void Upload(Req q, Res res, string space) {
  string meta = q.H("X-LB-Meta");
  if (meta.Length == 0 || meta.Length > 8192) { res.Json(new { error = "缺少文件信息" }, 400); return; }
  long length = q.Chunked ? MaxFile : q.Length;
  if (length <= 0) { res.Json(new { error = "文件是空的" }, 400); return; }
  if (length > MaxFile) { res.Close = true; res.Json(new { error = "小云一次最多放 " + (MaxFile >> 20) + " MB" }, 413); return; }
  string id = Crypto.Token(16), part = Bin(id) + ".part";
  lock (gate) { Evict(length); reserved += length; }
  long got = 0;
  try {
   using (var f = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16)) {
    var buf = new byte[1 << 16]; int n;
    while ((n = q.Body.Read(buf, 0, buf.Length)) > 0) { got += n; if (got > length) throw new ApiError(413, "小云一次最多放 " + (MaxFile >> 20) + " MB"); f.Write(buf, 0, n); }
   }
   if (!q.Chunked && got != length) throw new IOException("上传不完整");
   var e = new Entry { id = id, space = space, meta = meta, size = got, at = Util.Ms() };
   lock (gate) { reserved -= length; File.Move(part, Bin(id)); ix.files.Add(e); Evict(0); Save(); }
   res.Json(new { id, at = e.at, size = got });
  } catch {
   lock (gate) reserved -= length;
   try { File.Delete(part); } catch { }
   res.Close = true; throw;
  }
 }

 void Delete(Res res, string space, string id) {
  lock (gate) {
   var e = ix.files.FirstOrDefault(x => x.id == id && x.space == space);
   if (e != null) { ix.files.Remove(e); try { File.Delete(Bin(id)); } catch { } Save(); }
  }
  res.Json(new { ok = true });
 }

 void Download(Res res, string id) {
  Entry e; lock (gate) e = ix.files.FirstOrDefault(x => x.id == id);
  if (e == null) { res.Json(new { error = "文件已经不在了（小云满了会先删最早的）" }, 404); return; }
  FileStream f;
  try { f = new FileStream(Bin(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
  catch { res.Json(new { error = "文件已经不在了" }, 404); return; }
  using (f) {
   var o = res.Begin(200, "application/octet-stream", f.Length, new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" });
   var buf = new byte[1 << 16]; int n;
   while ((n = f.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
   o.Flush();
  }
 }

 static byte[] page;
 static void Page(Res res) {
  page ??= Embedded.Resource("web/pool.html") ?? Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8>下载页缺失");
  res.Bytes(page, "text/html; charset=utf-8", 200, new Dictionary<string, string> { ["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data: blob:; base-uri 'none'; form-action 'none'" });
 }
}

/// <summary>服务器上只当中转（不开窗口、不碰剪贴板）：LanBridge.exe --relay-server [--port 49330] [--pool 目录] [--pool-mb 1024]</summary>
static class RelayHost {
 public static int Run(string[] a) {
  int port = 49330; string bind = "0.0.0.0", pool = null, logDir = null; long poolMB = 1024, maxMB = 300;
  for (int i = 0; i < a.Length; i++) {
   switch (a[i]) {
    case "--port" when i + 1 < a.Length: port = int.Parse(a[++i]); break;
    case "--bind" when i + 1 < a.Length: bind = a[++i]; break;
    case "--pool" when i + 1 < a.Length: pool = a[++i]; break;
    case "--pool-mb" when i + 1 < a.Length: poolMB = long.Parse(a[++i]); break;
    case "--relay-max-mb" when i + 1 < a.Length: maxMB = long.Parse(a[++i]); break;
    case "--log" when i + 1 < a.Length: logDir = a[++i]; break;
   }
  }
  string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LanBridgeRelay");
  pool ??= Path.Combine(root, "pool"); logDir ??= root;
  Directory.CreateDirectory(logDir); Log.Init(logDir);
  using var mutex = new Mutex(false, "Global\\LanBridgeRelay-" + port);
  bool owned; try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
  if (!owned) { Console.WriteLine("已经在运行"); return 0; }
  ThreadPool.SetMinThreads(32, 16);
  var fp = poolMB > 0 ? new FilePool(pool, poolMB << 20, maxMB << 20) : null;
  using var server = new RelayServer(port, maxMB << 20, IPAddress.Parse(bind), fp);
  Log.Write("轻桥中转 " + Program.Version + " 在 " + bind + ":" + port + "，单次上限 " + maxMB + " MB" + (fp != null ? "，小云 " + poolMB + " MB（" + pool + "）" : ""));
  Console.WriteLine("轻桥中转在 http://" + bind + ":" + port);
  Thread.Sleep(Timeout.Infinite);
  return 0;
 }
}
