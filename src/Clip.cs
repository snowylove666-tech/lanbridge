using System;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LanBridge;

/// <summary>
/// 剪贴板：监听本机复制（WM_CLIPBOARDUPDATE），把同步来的内容写回去。
/// 所有读写都在界面线程（STA）。测试模式（--fake-clipboard）不碰真实剪贴板。
/// </summary>
sealed class ClipService {
 [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
 [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
 [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

 readonly App app; Watcher watcher; Timer debounce; uint suppress, lastSeq;
 public readonly bool Fake;
 public string FakeKind, FakeText; public string[] FakePaths;
 public const long MaxImagePixels = 50_000_000;

 public ClipService(App app) { this.app = app; Fake = app.Opt.FakeClipboard; }

 public void Start() {
  if (Fake) return;
  lastSeq = GetClipboardSequenceNumber(); // 启动前就在剪贴板上的东西不算新复制
  debounce = new Timer { Interval = 160 };
  debounce.Tick += (s, e) => { debounce.Stop(); try { Capture(); } catch (Exception ex) { Log.Write("读取剪贴板出错：" + ex.Message); } };
  watcher = new Watcher(() => { uint seq = GetClipboardSequenceNumber(); if (seq == suppress || seq == lastSeq) return; debounce.Stop(); debounce.Start(); });
 }
 public void Stop() { watcher?.Dispose(); debounce?.Dispose(); }

 void Capture() {
  uint seq = GetClipboardSequenceNumber();
  if (seq == suppress || seq == lastSeq) return;
  lastSeq = seq;
  if (app.C.paused) return;
  IDataObject d = null;
  for (int i = 0; i < 5 && d == null; i++) { try { d = Clipboard.GetDataObject(); } catch (ExternalException) { System.Threading.Thread.Sleep(40); } }
  if (d == null || Ignored(d)) return;

  if (d.GetDataPresent(DataFormats.FileDrop)) {
   var files = d.GetData(DataFormats.FileDrop) as string[];
   if (files == null || files.Length == 0 || files.Length > 100) return;
   // 复制了文件夹就不自动同步（太大也太慢）；要发文件夹可以拖到设备上，会自动打包。
   if (files.Any(f => !File.Exists(f))) return;
   app.Hub.LocalCopy("files", null, files);
   return;
  }
  if (d.GetDataPresent(DataFormats.UnicodeText)) {
   var text = d.GetData(DataFormats.UnicodeText) as string;
   if (!string.IsNullOrEmpty(text) && text.Length <= 4 * 1024 * 1024) app.Hub.LocalCopy("text", text, null);
   return;
  }
  var image = StageImage(d);
  if (image != null) app.Hub.LocalCopy("image", null, new[] { image });
 }

 /// <summary>密码管理器等会给复制内容打“别同步”的标记，照做。</summary>
 static bool Ignored(IDataObject d) {
  if (d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || d.GetDataPresent("Clipboard Viewer Ignore")) return true;
  foreach (var f in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" }) {
   if (!d.GetDataPresent(f)) continue;
   try { if (d.GetData(f) is MemoryStream ms && ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0) return true; } catch { }
  }
  return false;
 }

 string StageImage(IDataObject d) {
  string dir = Path.Combine(app.Root, "stage"); Directory.CreateDirectory(dir);
  string path = Util.Reserve(dir, "截图 " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png");
  try {
   if (d.GetDataPresent("PNG")) {
    byte[] png = null; var raw = d.GetData("PNG");
    if (raw is MemoryStream ms) png = ms.ToArray(); else if (raw is byte[] b) png = b;
    if (png != null && png.Length > 8 && png[0] == 137 && png[1] == 80) { File.WriteAllBytes(path, png); return path; }
   }
   if (d.GetDataPresent(DataFormats.Bitmap) && d.GetData(DataFormats.Bitmap) is Image img) {
    if ((long)img.Width * img.Height > MaxImagePixels) return null;
    using var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImageUnscaled(img, 0, 0); }
    bmp.Save(path, ImageFormat.Png);
    return path;
   }
   return null;
  } catch (Exception e) { Log.Write("截图读取失败：" + e.Message); try { File.Delete(path); } catch { } return null; }
  finally { Util.Release(path); }
 }

 // ───────────── 写剪贴板 ─────────────

 /// <summary>publish=true：像用户自己复制一样，写进去以后也同步给其他设备。</summary>
 public bool WriteText(string text, bool publish = false) {
  if (Fake) { FakeKind = "text"; FakeText = text; FakePaths = null; if (publish) app.Hub.LocalCopy("text", text, null); app.Changes.Bump(); return true; }
  return app.OnUi(() => { var d = new DataObject(); d.SetData(DataFormats.UnicodeText, true, text); return Set(d, publish); }, false);
 }

 public bool WriteImage(string path, bool publish = false) {
  if (Fake) { FakeKind = "image"; FakePaths = new[] { path }; FakeText = null; if (publish) app.Hub.LocalCopy("image", null, FakePaths); app.Changes.Bump(); return true; }
  return app.OnUi(() => {
   Bitmap bmp;
   try {
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var src = Image.FromStream(fs, true, true);
    if ((long)src.Width * src.Height > MaxImagePixels) return WriteFilesNow(new[] { path }, publish);
    bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp); g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImageUnscaled(src, 0, 0);
   } catch { return WriteFilesNow(new[] { path }, publish); } // 解不开的图片就当文件放
   using (bmp) {
    var d = new DataObject();
    d.SetData(DataFormats.Bitmap, true, bmp);
    using var png = new MemoryStream(); bmp.Save(png, ImageFormat.Png); d.SetData("PNG", false, new MemoryStream(png.ToArray()));
    using (var bm = new MemoryStream()) { bmp.Save(bm, ImageFormat.Bmp); var all = bm.ToArray(); var dib = new byte[all.Length - 14]; Buffer.BlockCopy(all, 14, dib, 0, dib.Length); d.SetData(DataFormats.Dib, false, new MemoryStream(dib)); }
    return Set(d, publish);
   }
  }, false);
 }

 public bool WriteFiles(string[] paths, bool publish = false) {
  if (Fake) { FakeKind = "files"; FakePaths = paths; FakeText = null; if (publish) app.Hub.LocalCopy("files", null, paths); app.Changes.Bump(); return true; }
  return app.OnUi(() => WriteFilesNow(paths, publish), false);
 }

 bool WriteFilesNow(string[] paths, bool publish) {
  var d = new DataObject(); var list = new StringCollection(); list.AddRange(paths); d.SetFileDropList(list);
  d.SetData("Preferred DropEffect", false, new MemoryStream(BitConverter.GetBytes(1))); // 复制，不是剪切
  return Set(d, publish);
 }

 bool Set(DataObject d, bool publish) {
  try {
   Clipboard.SetDataObject(d, true, 10, 60);
   if (!publish) suppress = GetClipboardSequenceNumber();
   return true;
  } catch (Exception e) { Log.Write("写剪贴板失败：" + e.Message); return false; }
 }

 /// <summary>只收消息的隐藏窗口，窗口关掉也能继续监听剪贴板。</summary>
 sealed class Watcher : NativeWindow, IDisposable {
  readonly Action changed;
  public Watcher(Action changed) { this.changed = changed; CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); AddClipboardFormatListener(Handle); }
  protected override void WndProc(ref Message m) { if (m.Msg == 0x031D) try { changed(); } catch { } base.WndProc(ref m); }
  public void Dispose() { try { RemoveClipboardFormatListener(Handle); DestroyHandle(); } catch { } }
 }
}
