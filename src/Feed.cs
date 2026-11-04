using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
 [ScriptIgnore] public string text;          // 剪贴板文字只放内存，不写盘
 [ScriptIgnore] public long bumped;
}

sealed class Feed {
 readonly App app; readonly List<FeedItem> items = new List<FeedItem>();
 string FilePath => Path.Combine(app.Root, "feed.json");
 public Feed(App app) {
  this.app = app;
  try {
   if (File.Exists(FilePath)) {
    var saved = J.Parse<List<FeedItem>>(File.ReadAllText(FilePath));
    if (saved != null) foreach (var f in saved) { if (f.status == "sending" || f.status == "receiving") { f.status = "failed"; f.note = "程序关闭时还没传完"; } items.Add(f); }
   }
  } catch { }
 }

 public FeedItem Add(FeedItem f) {
  lock (items) { f.time = f.time == 0 ? Util.Ms() : f.time; items.Insert(0, f); while (items.Count > 200) items.RemoveAt(items.Count - 1); }
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

 DateTime lastSave;
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
   List<FeedItem> keep; lock (items) keep = items.Where(x => x.kind != "text").Take(150).ToList();
   string tmp = FilePath + ".tmp"; File.WriteAllText(tmp, J.Str(keep));
   if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
  } catch { }
 }
}
