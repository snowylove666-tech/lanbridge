using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LanBridge;

/// <summary>手机发来的一条：文字、照片或文件。</summary>
public sealed class HistItem {
 public string id, phoneId, phone, kind, text; public long time;
 public List<FeedFile> files = new List<FeedFile>();
}

/// <summary>
/// 手机发来的内容的历史（电脑界面里“手机发来的内容”）。存 %LOCALAPPDATA%\LanBridge\phone-history.dat，
/// 和设置一样用当前 Windows 账户加密。最多 1000 条，文字总量超过 16 MB 时删最旧的。照片和文件本身在接收文件夹里，这里只记路径。
/// </summary>
sealed class PhoneHistory {
 readonly App app; readonly List<HistItem> items = new List<HistItem>();
 readonly object gate = new object();
 static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LanBridge/2/phone-history");
 string FilePath => Path.Combine(app.Root, "phone-history.dat");
 const int MaxItems = 1000, MaxText = 1 << 20; const long MaxTotal = 16L << 20;
 public long Rev;

 public PhoneHistory(App app) {
  this.app = app;
  try {
   if (File.Exists(FilePath)) {
    var list = J.Parse<List<HistItem>>(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser));
    if (list != null) items.AddRange(list.Where(x => x != null && x.id != null));
   }
  } catch (Exception e) { Log.Write("手机历史读不了，重新开始：" + e.Message); }
  foreach (var x in items) x.files ??= new List<FeedFile>();
  Rev = items.Count;
 }

 public void Add(Phone p, string kind, string text, IEnumerable<string> paths) {
  var it = new HistItem { id = Crypto.Token(8), phoneId = p.id, phone = p.name, kind = kind, time = Util.Ms() };
  if (text != null) it.text = text.Length > MaxText ? text.Substring(0, MaxText) : text;
  if (paths != null) foreach (var f in paths) { try { it.files.Add(new FeedFile { name = Path.GetFileName(f), path = f, size = new FileInfo(f).Length }); } catch { } }
  lock (gate) {
   // 同一部手机几秒内发同样的文字（比如网页和快捷指令都发了一次）只记一条。
   var last = items.LastOrDefault(x => x.phoneId == p.id);
   if (last != null && kind == "text" && last.kind == "text" && last.text == it.text && it.time - last.time < 10000) return;
   items.Add(it);
   while (items.Count > MaxItems) items.RemoveAt(0);
   long total = items.Sum(x => (long)(x.text?.Length ?? 0));
   while (total > MaxTotal && items.Count > 1) { total -= items[0].text?.Length ?? 0; items.RemoveAt(0); }
   Rev++;
  }
  Save(); app.Changes.Bump();
 }

 public HistItem Find(string id) { lock (gate) return items.FirstOrDefault(x => x.id == id); }

 public object Query(string q, string phoneId, long before, int limit) {
  lock (gate) {
   IEnumerable<HistItem> all = items.AsEnumerable().Reverse();
   var phones = items.GroupBy(x => x.phoneId).Select(g => new { id = g.Key, name = g.Last().phone, count = g.Count() }).ToList();
   if (!string.IsNullOrEmpty(phoneId)) all = all.Where(x => x.phoneId == phoneId);
   if (!string.IsNullOrWhiteSpace(q)) all = all.Where(x => (x.text ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || x.files.Any(f => f.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
   if (before > 0) all = all.Where(x => x.time < before);
   var page = all.Take(limit + 1).ToList();
   return new {
    rev = Rev, total = items.Count, phones, more = page.Count > limit,
    items = page.Take(limit).Select(x => new {
     x.id, x.phoneId, x.phone, x.kind, x.time, text = Util.Clip(x.text, 6000), chars = x.text?.Length ?? 0,
     files = x.files.Select(f => new { f.name, f.size, exists = f.path != null && File.Exists(f.path) }).ToList()
    }).ToList()
   };
  }
 }

 public void Remove(string id) { lock (gate) { items.RemoveAll(x => x.id == id); Rev++; } Save(); app.Changes.Bump(); }
 /// <summary>几张卡片并成一部手机时，历史也归到留下的那张。</summary>
 public void Merge(List<string> from, string to) {
  lock (gate) { foreach (var x in items) if (from.Contains(x.phoneId)) x.phoneId = to; Rev++; }
  Save(); app.Changes.Bump();
 }

 public void Clear(string phoneId) { lock (gate) { items.RemoveAll(x => string.IsNullOrEmpty(phoneId) || x.phoneId == phoneId); Rev++; } Save(); app.Changes.Bump(); }
 public int Count { get { lock (gate) return items.Count; } }

 void Save() {
  try {
   byte[] bytes; lock (gate) bytes = ProtectedData.Protect(J.Bytes(items), Entropy, DataProtectionScope.CurrentUser);
   string tmp = FilePath + ".tmp"; File.WriteAllBytes(tmp, bytes);
   if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
  } catch (Exception e) { Log.Write("手机历史保存失败：" + e.Message); }
 }
}
