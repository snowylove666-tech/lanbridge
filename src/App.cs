using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace LanBridge;

public sealed class Options {
 public string Data, Name; public int Port = 49321; public string[] Send;
 public bool Background, Loopback, FakeClipboard, Headless, NoShell, Dev, NoLan, FakeTunnel, Guard;
 public string RootDir => Data ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge");
 public string Relay, RelayDefault;
 /// <summary>临时中转单次上限（Cloudflare 免费版一次最多 100 MB）；--relay-host 测试用的独立中转端口。</summary>
 public long RelayMaxMB = 95; public int RelayHost;
 public List<string> Scan = new List<string>();
 public static Options Parse(string[] a) {
  var o = new Options();
  for (int i = 0; i < a.Length; i++) {
   switch (a[i]) {
    case "--data" when i + 1 < a.Length: o.Data = Path.GetFullPath(a[++i]); break;
    case "--port" when i + 1 < a.Length: o.Port = int.Parse(a[++i]); break;
    case "--name" when i + 1 < a.Length: o.Name = a[++i]; break;
    case "--scan" when i + 1 < a.Length: o.Scan.Add(a[++i]); break;
    case "--background": o.Background = true; break;
    case "--guard": o.Guard = true; o.Background = true; break;
    case "--loopback": o.Loopback = true; break;
    case "--fake-clipboard": o.FakeClipboard = true; break;
    case "--headless": o.Headless = true; o.Background = true; break;
    case "--no-shell": o.NoShell = true; break;
    case "--dev": o.Dev = true; break;
    case "--relay" when i + 1 < a.Length: o.Relay = a[++i]; break;
    case "--no-lan": o.NoLan = true; break;
    case "--builtin-relay" when i + 1 < a.Length: o.RelayDefault = a[++i]; break;
    case "--fake-tunnel": o.FakeTunnel = true; break;
    case "--relay-max-mb" when i + 1 < a.Length: o.RelayMaxMB = long.Parse(a[++i]); break;
    case "--relay-host" when i + 1 < a.Length: o.RelayHost = int.Parse(a[++i]); break;
    case "--send": o.Send = a.Skip(i + 1).ToArray(); i = a.Length; break;
   }
  }
  if (o.Port < 1024 || o.Port > 65535) o.Port = 49321;
  // 测试实例（自定义数据目录）不改系统设置。
  if (o.Data != null) o.NoShell = true;
  return o;
 }
}

/// <summary>整个程序。界面线程上跑：托盘、剪贴板监听、主窗口（按需创建，关掉就释放）。</summary>
sealed class App : ApplicationContext {
 public readonly Options Opt; public readonly Store Store; public Config C => Store.C;
 public readonly object Gate = new object();
 public readonly string Root, LocalToken = Crypto.Token(16), MdnsName;
 public readonly Changes Changes = new Changes();
 public readonly Feed Feed; public readonly Hub Hub; public readonly PhoneHub Phones; public readonly ClipService Clip; public readonly Discovery Discovery; public readonly Relay Relay; public readonly TempRelay Temp; public readonly Cloud Cloud; public readonly PhoneHistory History; public readonly ClipLog ClipLog;
 readonly HttpServer server; readonly Mdns mdns; RelayServer testRelay; readonly CommandPipe pipe; readonly Tray tray; readonly Control invoker;
 MainWindow window;
 public Icon AppIcon;
 public string[] PendingSend; public long PendingSendAt;
 public bool WebViewFailed;
 bool closedOnce;

 public App(Options o) {
  Opt = o;
  Root = o.RootDir;
  Directory.CreateDirectory(Root); Log.Init(Root);
  Store = new Store(Root, o);
  MdnsName = "lanbridge-" + Crypto.Sha(C.deviceId).Substring(0, 6) + ".local";
  invoker = new Control(); invoker.CreateControl(); _ = invoker.Handle;
  try { var b = Embedded.Resource("app.ico"); AppIcon = b == null ? SystemIcons.Application : new Icon(new MemoryStream(b)); } catch { AppIcon = SystemIcons.Application; }
  ThreadPool.SetMinThreads(64, 32);

  Feed = new Feed(this); History = new PhoneHistory(this); ClipLog = new ClipLog(this); Clip = new ClipService(this); Hub = new Hub(this); Phones = new PhoneHub(this); Discovery = new Discovery(this); Relay = new Relay(this); Temp = new TempRelay(this); Cloud = new Cloud(this);
  var router = new Router(this);
  server = new HttpServer(o.Loopback ? IPAddress.Loopback : IPAddress.Any, o.Port, router.Handle);
  server.Start();
  Clip.Start(); Hub.Start(); if (!o.NoLan) Discovery.Start(); Relay.Start(router.Handle); Temp.Resume();
  if (o.RelayHost > 0) testRelay = new RelayServer(o.RelayHost, o.RelayMaxMB << 20);
  if (!o.Loopback) { mdns = new Mdns(MdnsName); mdns.Start(); }
  pipe = new CommandPipe(o.Port, (cmd, files) => Ui(() => OnCommand(cmd, files)));
  try { File.WriteAllText(Path.Combine(Root, "desk-url.txt"), DeskUrl); } catch { }

  if (!o.NoShell && !C.shellApplied) { Shell.SetAutostart(C.autostart); Shell.SetSendTo(C.sendTo); C.shellApplied = true; Save(); }
  // 2.2.25 换了白云图标：“发送到”里的快捷方式重建一次，资源管理器刷新图标
  const int IconVer = 2;
  if (!o.NoShell && C.iconVer != IconVer) { if (C.sendTo) Shell.SetSendTo(true); Shell.IconsChanged(); C.iconVer = IconVer; Save(); }
  // 守护任务跟着“开机自动启动”走；老版本装的没有它，每次启动补一下。
  if (!o.NoShell) { bool on = C.autostart; System.Threading.ThreadPool.QueueUserWorkItem(_ => Shell.SetGuard(on)); }
  if (!o.Headless) tray = new Tray(this);
  if (!o.Background) ShowWindow();
  if (o.Send != null) Ui(() => OnCommand("send", o.Send));
  Log.Write("轻桥 " + Program.Version + " 启动，端口 " + o.Port + "，设备 " + C.deviceName);
 }

 public string DeskUrl => "http://127.0.0.1:" + Opt.Port + "/desk#t=" + LocalToken;
 public void Save() { lock (Gate) Store.Save(); }

 public void Ui(Action a) { try { if (invoker.InvokeRequired) invoker.BeginInvoke(a); else a(); } catch { } }
 public T OnUi<T>(Func<T> f, T fallback, int ms = 6000) {
  if (!invoker.InvokeRequired) return f();
  try {
   var ar = invoker.BeginInvoke(f);
   if (!ar.AsyncWaitHandle.WaitOne(ms)) return fallback;
   return (T)invoker.EndInvoke(ar);
  } catch (Exception e) { Log.Write("界面线程调用失败：" + e.Message); return fallback; }
 }

 public void ShowWindow() => Ui(() => {
  if (Opt.Headless) return;
  if (window == null || window.IsDisposed) {
   window = new MainWindow(this);
   window.FormClosed += (s, e) => {
    window = null;
    if (!closedOnce && !exiting) { closedOnce = true; Notify("轻桥还在后台运行", "复制和收文件照常进行。点任务栏右下角的图标可以再打开。", ShowWindow); }
    GC.Collect();
   };
  }
  window.Reveal();
 });

 public IWin32Window Owner => window != null && !window.IsDisposed ? window : null;

 public void Notify(string title, string text, Action click) { if (tray != null) Ui(() => tray.Balloon(title, text, click)); }

 /// <summary>需要人处理的事（有人请求连接）：弹窗口 + 通知。</summary>
 public void Attention(string title, string text) { ShowWindow(); Notify(title, text, ShowWindow); }

 public void SetPaused(bool on) { lock (Gate) C.paused = on; Save(); Changes.Bump(); tray?.Refresh(); }

 public void SendTo(string target, string[] paths) {
  if (paths == null || paths.Length == 0) throw new ApiError(400, "没有选文件");
  if (target == "cloud") Cloud.Upload(paths);
  else if (target.StartsWith("phone:")) Phones.Offer(target.Substring(6), paths);
  else Hub.SendFiles(target, paths);
 }

 void OnCommand(string cmd, string[] files) {
  // 守护任务每 5 分钟用 --background 叫一次，只是确认轻桥活着：不能弹窗口（以前会让任务栏图标一闪一闪）。
  if (cmd == "noop") return;
  if (cmd == "send" && files != null && files.Length > 0) {
   // 只有一台电脑在线、也没连手机时直接发；否则打开窗口让人选。
   var online = Hub.Links().Where(l => l.State != "offline").ToList();
   if (online.Count == 1 && C.phones.Count == 0) { try { SendTo(online[0].Id, files); Notify("正在发给 " + online[0].M.name, files.Length + " 个文件", ShowWindow); return; } catch { } }
   PendingSend = files; PendingSendAt = Util.Ms(); Changes.Bump();
  }
  ShowWindow();
 }

 bool exiting;
 /// <summary>用户自己点的退出：留个记号，守护任务就不会把它再开起来。</summary>
 public bool UserExit;
 public void Exit() {
  exiting = true; UserExit = true;
  try { File.WriteAllText(Path.Combine(Root, "quit.flag"), DateTime.Now.ToString("s")); } catch { }
  window?.Close(); ExitThread();
 }

 public void Shutdown() {
  exiting = true;
  try { Discovery.Stop(); } catch { }
  try { Relay.Stop(); } catch { }
  try { Temp.Shutdown(); } catch { }
  try { testRelay?.Dispose(); } catch { }
  try { Hub.Stop(); } catch { }
  try { server.Dispose(); } catch { }
  try { mdns?.Dispose(); } catch { }
  try { pipe.Dispose(); } catch { }
  try { Clip.Stop(); } catch { }
  try { tray?.Dispose(); } catch { }
 }
}
