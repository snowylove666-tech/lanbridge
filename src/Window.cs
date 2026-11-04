using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LanBridge;

/// <summary>主窗口：一个 WebView2，界面就是 web/desk.html。关窗口即释放（省内存），托盘继续运行。</summary>
sealed class MainWindow : Form {
 readonly App app; readonly WebView2 web;
 [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
 static readonly Color Light = Color.FromArgb(247, 246, 243), Dark = Color.FromArgb(28, 28, 30);

 public MainWindow(App app) {
  this.app = app;
  bool dark = Shell.DarkMode();
  Text = "轻桥"; Icon = app.AppIcon; StartPosition = FormStartPosition.CenterScreen;
  AutoScaleMode = AutoScaleMode.None;
  // .NET 4.8 的 WinForms 在没有 app.config 时总报 96 DPI；直接问 Windows 这个窗口所在屏幕的缩放（否则 200% 屏幕上窗口只有一半大）。
  float k = 1f;
  try { uint dpi = GetDpiForWindow(Handle); if (dpi >= 96) k = dpi / 96f; } catch { using var g = CreateGraphics(); k = g.DpiX / 96f; }
  ClientSize = new Size((int)(980 * k), (int)(700 * k)); MinimumSize = new Size((int)(560 * k), (int)(480 * k));
  var area = Screen.FromPoint(Cursor.Position).WorkingArea;
  if (Height > area.Height - 40) Height = area.Height - 40;
  BackColor = dark ? Dark : Light;
  web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
  Controls.Add(web);
  HandleCreated += (s, e) => Shell.TitleBar(Handle, dark, BackColor);
  Load += async (s, e) => {
   try {
    CoreWebView2Environment.SetLoaderDllFolderPath(Embedded.LoaderFolder(app.Root));
    var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(app.Root, "webview"), new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection,msEdgeTranslate" });
    await web.EnsureCoreWebView2Async(env);
    var w = web.CoreWebView2;
    w.Settings.IsStatusBarEnabled = false; w.Settings.IsZoomControlEnabled = false;
    w.Settings.AreDevToolsEnabled = app.Opt.Dev; w.Settings.AreBrowserAcceleratorKeysEnabled = app.Opt.Dev;
    w.Settings.IsGeneralAutofillEnabled = false; w.Settings.IsPasswordAutosaveEnabled = false;
    w.Settings.AreDefaultContextMenusEnabled = true;
    w.ContextMenuRequested += (o, a) => { if (!a.ContextMenuTarget.IsEditable && !a.ContextMenuTarget.HasSelection) a.Handled = true; };
    w.NewWindowRequested += (o, a) => { a.Handled = true; try { System.Diagnostics.Process.Start(a.Uri); } catch { } };
    w.NavigationStarting += (o, a) => { if (!a.Uri.StartsWith("http://127.0.0.1:" + app.Opt.Port + "/")) a.Cancel = true; };
    w.WebMessageReceived += OnMessage;
    w.Navigate(app.DeskUrl);
   } catch (Exception ex) {
    // 没有 WebView2 运行时（极少见，老 Win10）：用浏览器打开同一个界面。
    Log.Write("WebView2 不可用：" + ex.Message);
    app.WebViewFailed = true;
    try { System.Diagnostics.Process.Start(app.DeskUrl); } catch { }
    BeginInvoke((Action)Close);
   }
  };
 }

 /// <summary>页面里拖进来的文件：WebView2 把真实路径交给我们，直接发，不用先复制一份。</summary>
 void OnMessage(object s, CoreWebView2WebMessageReceivedEventArgs e) {
  try {
   var d = J.Obj(e.WebMessageAsJson);
   if (d.Get("cmd") != "drop") return;
   var paths = new List<string>();
   if (e.AdditionalObjects != null) foreach (var o in e.AdditionalObjects) if (o is CoreWebView2File f && !string.IsNullOrEmpty(f.Path)) paths.Add(f.Path);
   if (paths.Count > 0) app.SendTo(d.Get("target"), paths.ToArray());
  } catch (Exception ex) {
   try { web.CoreWebView2.PostWebMessageAsJson(J.Str(new { error = ex.Message })); } catch { }
  }
 }

 public void Reveal() {
  if (!Visible) Show();
  if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
  Activate(); BringToFront();
 }
}

/// <summary>托盘图标和菜单。</summary>
sealed class Tray : IDisposable {
 readonly App app; readonly NotifyIcon icon; readonly ToolStripMenuItem pause; Action balloon;
 public Tray(App app) {
  this.app = app;
  var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9f) };
  menu.Items.Add("打开轻桥", null, (s, e) => app.ShowWindow()).Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
  pause = new ToolStripMenuItem("暂停同步", null, (s, e) => app.SetPaused(!app.C.paused));
  menu.Items.Add(pause);
  menu.Items.Add("打开收到的文件", null, (s, e) => Shell.OpenFolder(app.C.receiveFolder));
  menu.Items.Add(new ToolStripSeparator());
  menu.Items.Add("退出轻桥", null, (s, e) => app.Exit());
  menu.Opening += (s, e) => Refresh();
  icon = new NotifyIcon { Icon = new Icon(app.AppIcon, SystemInformation.SmallIconSize), Text = "轻桥", Visible = true, ContextMenuStrip = menu };
  icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) app.ShowWindow(); };
  icon.BalloonTipClicked += (s, e) => { var a = balloon; balloon = null; a?.Invoke(); };
  Refresh();
 }
 public void Refresh() {
  pause.Text = app.C.paused ? "恢复同步" : "暂停同步";
  int online = app.Hub.Links().Count(l => l.State != "offline");
  string t = app.C.paused ? "轻桥 · 已暂停" : online > 0 ? "轻桥 · 已连接 " + online + " 台电脑" : "轻桥";
  icon.Text = t.Length > 63 ? t.Substring(0, 63) : t;
 }
 public void Balloon(string title, string text, Action click) { balloon = click; icon.ShowBalloonTip(6000, title, text, ToolTipIcon.None); }
 public void Dispose() { icon.Visible = false; icon.Dispose(); }
}

/// <summary>轻桥的标志：渐变方块上一座白色拱桥。界面、托盘、手机主屏图标都用它。</summary>
static class Brand {
 public static Bitmap Render(int s, bool rounded = true) {
  var b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
  using var g = Graphics.FromImage(b);
  g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
  float k = s / 64f;
  using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), Color.FromArgb(0x3d, 0x6b, 0xf5), Color.FromArgb(0x19, 0xa9, 0x9a))) {
   if (rounded) { using var path = Round(0, 0, s, s, 14 * k); g.FillPath(bg, path); } else g.FillRectangle(bg, 0, 0, s, s);
  }
  using (var deck = new Pen(Color.FromArgb(150, 255, 255, 255), 5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(deck, 11 * k, 45 * k, 53 * k, 45 * k);
  using (var arch = new Pen(Color.White, 5.5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawBezier(arch, 15 * k, 43 * k, 25 * k, 20 * k, 39 * k, 20 * k, 49 * k, 43 * k);
  using (var dot = new SolidBrush(Color.White)) { g.FillEllipse(dot, (15 - 4.5f) * k, (43 - 4.5f) * k, 9 * k, 9 * k); g.FillEllipse(dot, (49 - 4.5f) * k, (43 - 4.5f) * k, 9 * k, 9 * k); }
  return b;
 }
 static GraphicsPath Round(float x, float y, float w, float h, float r) {
  var p = new GraphicsPath(); float d = r * 2;
  p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90); p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90); p.CloseFigure(); return p;
 }
 static byte[] touch;
 public static byte[] TouchIcon() {
  if (touch != null) return touch;
  using var b = Render(180, false); using var m = new MemoryStream(); b.Save(m, ImageFormat.Png); return touch = m.ToArray();
 }
 /// <summary>生成多尺寸 .ico（构建时用：LanBridge.exe --make-icon LanBridge.ico）。</summary>
 public static void WriteIco(string path) {
  int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
  var pngs = sizes.Select(sz => { using var b = Render(sz); using var m = new MemoryStream(); b.Save(m, ImageFormat.Png); return m.ToArray(); }).ToList();
  using var f = new BinaryWriter(File.Create(path));
  f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
  int offset = 6 + 16 * sizes.Length;
  for (int i = 0; i < sizes.Length; i++) {
   f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); f.Write((byte)0); f.Write((byte)0);
   f.Write((short)1); f.Write((short)32); f.Write(pngs[i].Length); f.Write(offset); offset += pngs[i].Length;
  }
  foreach (var p in pngs) f.Write(p);
 }
}
