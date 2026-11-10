using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace LanBridge;

public sealed class FeedFile { public string name, path; public long size; }

/// <summary>“动态”里的一条：一次剪贴板同步，或一次发文件。</summary>
public sealed class FeedItem {
 public string id, kind, dir, from, status, note;
 public List<string> to = new List<string>();
 public List<FeedFile> files = new List<FeedFile>();
 public long time, total, done;
 public bool image, clip;
 public string text;                         // 2.2.5 起文字也存下来（feed.dat 用当前 Windows 账户加密），重启后记录还在
 [ScriptIgnore] public long bumped;
}

sealed class Feed {
 readonly App app; readonly List<FeedItem> items = new List<FeedItem>();
 string OldPath => Path.Combine(app.Root, "feed.json");
 string FilePath => Path.Combine(app.Root, "feed.dat");
 static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LanBridge/2/feed");
 const int MaxItems = 3000, MaxSavedText = 20000;
 public int Count { get { lock (items) return items.Count; } }
 /// <summary>按设置里的保留天数删掉太早的记录（只删记录，不删收到的文件）。</summary>
 public void Prune() { if (PruneNow()) Changed(true); }
 bool PruneNow() {
  int days = app.C.feedDays; if (days <= 0) return false;
  long cut = Util.Ms() - days * 86400000L;
  lock (items) return items.RemoveAll(x => x.time < cut && x.status != "sending" && x.status != "receiving") > 0;
 }
 public Feed(App app) {
  this.app = app;
  try {
   List<FeedItem> saved = null;
   if (File.Exists(FilePath)) saved = J.Parse<List<FeedItem>>(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser));
   else if (File.Exists(OldPath)) saved = J.Parse<List<FeedItem>>(File.ReadAllText(OldPath));
   {
    if (saved != null) foreach (var f in saved) { if (f.status == "sending" || f.status == "receiving") { f.status = "failed"; f.note = "程序关闭时还没传完"; } items.Add(f); }
   }
  } catch (Exception e) { Log.Write("动态记录读不了，重新开始：" + e.Message); }
  PruneNow();
 }

 public FeedItem Add(FeedItem f) {
  lock (items) { f.time = f.time == 0 ? Util.Ms() : f.time; items.Insert(0, f); while (items.Count > MaxItems) items.RemoveAt(items.Count - 1); }
  if (DateTime.UtcNow - lastPrune > TimeSpan.FromHours(1)) { lastPrune = DateTime.UtcNow; PruneNow(); }
  Changed(true); return f;
 }
 public FeedItem Find(string id) { lock (items) return items.FirstOrDefault(x => x.id == id); }
 public void Update(string id, Action<FeedItem> change, bool persist = true) {
  FeedItem f; lock (items) { f = items.FirstOrDefault(x => x.id == id); if (f == null) return; change(f); }
  Changed(persist);
 }
 /// <summary>传输进度：最多每 250ms 刷新一次界面。</summary>
 public void Progress(string id, long done) {
  FeedItem f; bool bump = false;
  lock (items) { f = items.FirstOrDefault(x => x.id == id); if (f == null) return; f.done = done; long now = Util.Ms(); if (now - f.bumped > 250) { f.bumped = now; bump = true; } }
  if (bump) app.Changes.Bump();
 }
 public void Remove(string id) { lock (items) items.RemoveAll(x => x.id == id); Changed(true); }
 public void Clear() { lock (items) items.Clear(); Changed(true); }
 public List<FeedItem> Snapshot() { lock (items) return items.ToList(); }

 DateTime lastSave, lastPrune;
 void Changed(bool persist) {
  app.Changes.Bump();
  if (!persist) return;
  // 文字条目不落盘；其余（文件、图片）落盘，方便重启后还能找到收到的东西。
  if (DateTime.UtcNow - lastSave < TimeSpan.FromMilliseconds(500)) { ScheduleSave(); return; }
  Save();
 }
 bool scheduled;
 void ScheduleSave() {
  lock (items) { if (scheduled) return; scheduled = true; }
  Util.Thread("feed-save", () => { System.Threading.Thread.Sleep(800); lock (items) scheduled = false; Save(); });
 }
 void Save() {
  lastSave = DateTime.UtcNow;
  try {
   byte[] bytes;
   lock (items) {
    var keep = items.Select(x => x.text != null && x.text.Length > MaxSavedText
     ? new FeedItem { id = x.id, kind = x.kind, dir = x.dir, from = x.from, status = x.status, note = x.note, to = x.to, files = x.files, time = x.time, total = x.total, done = x.done, image = x.image, clip = x.clip, text = x.text.Substring(0, MaxSavedText) } : x).ToList();
    bytes = ProtectedData.Protect(J.Bytes(keep), Entropy, DataProtectionScope.CurrentUser);
   }
   string tmp = FilePath + ".tmp"; File.WriteAllBytes(tmp, bytes);
   if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
   try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
  } catch { }
 }
}
