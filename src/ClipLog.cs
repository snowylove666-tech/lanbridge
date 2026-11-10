using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LanBridge;

public sealed class ClipEntry {
 public string id, kind, text, from; public long time;
 public List<FeedFile> files = new List<FeedFile>();
}

/// <summary>
/// 电脑剪贴板的历史，给手机网页的“之前的”用：手机上不只看到最新一条，之前复制的文字、图片、文件也能翻到。
/// 存 %LOCALAPPDATA%\LanBridge\clip-log.dat，用当前 Windows 账户加密；只留最近 300 条、30 天内的。
/// 每部手机自己的“清空”和“自动清理天数”只是不再给它看，记在 Phone 上。
/// </summary>
sealed class ClipLog {
 readonly App app; readonly object gate = new object();
 readonly List<ClipEntry> items = new List<ClipEntry>();
 static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LanBridge/2/clip-log");
 const int MaxItems = 300, MaxText = 20000; const long MaxAge = 30L * 86400000;
 string FilePath => Path.Combine(app.Root, "clip-log.dat");
 public long Rev;

 public ClipLog(App app) {
  this.app = app;
  try {
   if (File.Exists(FilePath)) {
    var list = J.Parse<List<ClipEntry>>(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser));
    if (list != null) items.AddRange(list.Where(x => x != null && x.id != null));
   }
  } catch (Exception e) { Log.Write("剪贴板历史读不了，重新开始：" + e.Message); }
  foreach (var x in items) x.files ??= new List<FeedFile>();
  Rev = items.Count;
 }

 public void Add(SyncEvent ev, string[] paths) {
  if (ev == null) return;
  var it = new ClipEntry { id = ev.id, kind = ev.kind, from = ev.via ?? ev.originName, time = ev.stamp > 0 ? ev.stamp : Util.Ms() };
  if (ev.text != null) it.text = ev.text.Length > MaxText ? ev.text.Substring(0, MaxText) : ev.text;
  if (paths != null) for (int i = 0; i < paths.Length; i++) { try { it.files.Add(new FeedFile { name = i < ev.items.Count ? ev.items[i].name : Path.GetFileName(paths[i]), path = paths[i], size = new FileInfo(paths[i]).Length }); } catch { } }
  lock (gate) {
   if (items.Any(x => x.id == it.id)) return;
   var last = items.LastOrDefault();
   if (last != null && it.kind == "text" && last.kind == "text" && last.text == it.text) { last.time = it.time; last.id = it.id; }
   else items.Add(it);
   long old = Util.Ms() - MaxAge;
   items.RemoveAll(x => x.time < old);
   while (items.Count > MaxItems) items.RemoveAt(0);
   Rev++;
  }
  Save();
 }

 public ClipEntry Find(string id) { lock (gate) return items.FirstOrDefault(x => x.id == id); }

 /// <summary>给一部手机看的历史：新的在前；它自己发来的不算；清空过、超过自动清理天数的不给。</summary>
 public object For(Phone p) {
  long now = Util.Ms(), cut = Math.Max(p.histFrom, p.histDays > 0 ? now - p.histDays * 86400000L : 0);
  lock (gate) {
   var list = items.Where(x => x.time > cut && x.from != p.name).Reverse().Take(120).ToList();
   return new {
    rev = Rev, days = p.histDays,
    items = list.Select(x => new {
     x.id, x.kind, x.time, x.from, text = x.text,
     files = x.files.Select(f => new { f.name, f.size, ok = f.path != null && File.Exists(f.path) }).ToList()
    }).ToList()
   };
  }
 }

 void Save() {
  try {
   byte[] bytes; lock (gate) bytes = ProtectedData.Protect(J.Bytes(items), Entropy, DataProtectionScope.CurrentUser);
   string tmp = FilePath + ".tmp"; File.WriteAllBytes(tmp, bytes);
   if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
  } catch (Exception e) { Log.Write("剪贴板历史没存上：" + e.Message); }
 }
}
