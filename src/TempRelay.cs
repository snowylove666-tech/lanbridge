using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;

namespace LanBridge;

/// <summary>
/// 临时中转：这台电脑自己当中转（RelayServer），再用 Cloudflare 的免费临时隧道（cloudflared，免注册）连到外网。
/// 隧道地址每次启动都不一样：拿到新地址后写进设置，组里的电脑跟着换；别的网络里只经它相连的设备要重新输入连接码。
/// 第一次用时从 GitHub 下载 Cloudflare 官方的 cloudflared.exe，校验 Cloudflare 的数字签名后才运行。
/// </summary>
sealed class TempRelay {
 const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-";
 static readonly Regex UrlRx = new Regex(@"https://(?!api\.)[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);

 readonly App app; Config C => app.C;
 readonly object gate = new object();
 RelayServer server; Process proc; Thread loop; volatile bool wanted; readonly AutoResetEvent wake = new AutoResetEvent(false);
 /// <summary>off / download / starting / ok / error</summary>
 public volatile string State = "off", Error, Url;
 public volatile int Progress;
 public long Since;
 int rotate;

 public TempRelay(App app) { this.app = app; }
 public bool On => C.relayTemp;
 string Tool => Path.Combine(app.Root, "tools", "cloudflared.exe");
 public bool ToolReady => File.Exists(Tool);

 /// <summary>开机时：上次开着临时中转就接着开。</summary>
 public void Resume() { if (C.relayTemp) Start(); }

 public void Start() {
  lock (gate) {
   wanted = true;
   if (loop != null && loop.IsAlive) { wake.Set(); return; }
   loop = new Thread(() => { try { Loop(); } catch (Exception e) { Log.Write("临时中转退出：" + e); } }) { IsBackground = true, Name = "temp-relay" };
   loop.Start();
  }
 }

 public void Stop() {
  lock (gate) { wanted = false; wake.Set(); }
  Kill();
  lock (gate) { try { server?.Dispose(); } catch { } server = null; }
  Relay.LocalFrom = Relay.LocalTo = null;
  Url = null; Set("off", null);
 }

 /// <summary>测试用：换一个隧道地址（模拟重启后地址变了）。</summary>
 public void Rotate() { Interlocked.Increment(ref rotate); Kill(); wake.Set(); }

 void Set(string s, string err) { bool ch = State != s || Error != err; State = s; Error = err; if (ch) app.Changes.Bump(); }

 void Loop() {
  int fails = 0;
  while (wanted) {
   try {
    EnsureServer();
    string url = app.Opt.FakeTunnel ? FakeTunnel() : RealTunnel();
    if (!wanted) break;
    fails = 0; Publish(url); Set("ok", null);
    Watch(url);
   } catch (Exception e) {
    if (!wanted) break;
    fails++; Url = null;
    Set("error", e is ApiError ? e.Message : "临时隧道没开起来：" + e.Message);
    Log.Write("临时中转：" + e.Message);
    wake.WaitOne(Math.Min(5000 * fails, 60000));
   }
  }
 }

 void EnsureServer() {
  lock (gate) {
   if (server != null) return;
   // 端口尽量固定（重启后隧道还指向同一个），被占了就随便挑一个。
   int want = app.Opt.Port + 9;
   try { server = new RelayServer(want, app.Opt.RelayMaxMB << 20); } catch { server = new RelayServer(0, app.Opt.RelayMaxMB << 20); }
  }
 }
 string Local => "http://127.0.0.1:" + server.Port;

 // 测试时不连 Cloudflare：直接用本机地址当“隧道地址”；Rotate 时换一个端口，模拟地址变了。
 string FakeTunnel() {
  if (rotate > 0) {
   lock (gate) { try { server?.Dispose(); } catch { } server = null; }
   rotate = 0;
   lock (gate) server = new RelayServer(0, app.Opt.RelayMaxMB << 20);
  }
  return Local;
 }

 string RealTunnel() {
  if (!ToolReady) Download();
  Set("starting", null);
  Kill();
  var psi = new ProcessStartInfo(Tool, "tunnel --no-autoupdate --protocol http2 --url " + Local) {
   UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(Tool)
  };
  // 用户目录里有 cloudflared 的配置文件时，临时隧道会去读它：换一个空的目录当“家”。
  string home = Path.Combine(app.Root, "tools", "home"); Directory.CreateDirectory(home);
  psi.EnvironmentVariables["USERPROFILE"] = home; psi.EnvironmentVariables["HOME"] = home;
  string found = null; bool registered = false; string lastLine = "";
  var ready = new ManualResetEventSlim(false);
  var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
  DataReceivedEventHandler onLine = (s, e) => {
   if (e.Data == null) return;
   lastLine = e.Data;
   var m = UrlRx.Match(e.Data); if (m.Success && found == null) found = m.Value.ToLowerInvariant();
   if (e.Data.IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) >= 0) registered = true;
   if (found != null && registered) ready.Set();
  };
  p.ErrorDataReceived += onLine; p.OutputDataReceived += onLine;
  p.Exited += (s, e) => { ready.Set(); wake.Set(); };
  p.Start(); Job.Add(p);
  p.BeginErrorReadLine(); p.BeginOutputReadLine();
  lock (gate) proc = p;
  ready.Wait(60000);
  if (p.HasExited) throw new ApiError(502, "Cloudflare 临时隧道没开起来" + (lastLine.Length > 0 ? "（" + Util.Clip(Clean(lastLine), 120) + "）" : "") + "。国内网络有时连不上 Cloudflare，可以稍后再试，或改用常驻中转");
  if (found == null || !registered) { Kill(); throw new ApiError(504, "Cloudflare 一分钟没给隧道地址，可能是网络连不上 Cloudflare"); }
  // 新地址刚建好，DNS 要几秒才生效：等它能打开（最多 30 秒，打不开也先用着，本机走本地不受影响）。
  long end = Util.Ms() + 30000;
  while (Util.Ms() < end && wanted && !p.HasExited) { if (Healthy(found, 5000)) break; wake.WaitOne(1500); }
  return found;
 }

 static string Clean(string line) { int i = line.IndexOf(" ERR "); if (i < 0) i = line.IndexOf(" INF "); return (i >= 0 ? line.Substring(i + 5) : line).Trim(); }

 static bool Healthy(string url, int ms) {
  try { var d = J.Obj(Client.Send(Relay.Make(url + "/health", "GET", ms, viaPublic: true), null)); return d.Get("app") == "LanBridge relay"; } catch { return false; }
 }

 /// <summary>拿到新地址：写进设置（组里的电脑经现有的路跟着换），这台电脑自己经本地连。</summary>
 void Publish(string url) {
  Url = url; Since = Util.Ms();
  Relay.LocalFrom = url; Relay.LocalTo = Local;
  string old; bool changed;
  lock (app.Gate) { old = C.relay; changed = C.relay != url; if (changed) { C.relay = url; C.relayAt = Util.Ms(); } }
  if (!changed) { app.Relay.Kick(); return; }
  app.Save(); app.Changes.Bump();
  bool wasTemp = old != null && (old.Contains(".trycloudflare.com") || app.Opt.FakeTunnel) && old.Length > 0;
  Util.Thread("relay-announce", () => {
   app.Hub.AnnounceRelay(); app.Relay.Kick();
   foreach (var l in app.Hub.Links()) { l.LastHello = 0; l.Signal.Set(); }
  });
  if (wasTemp) {
   // 地址变了：只经旧地址相连的设备收不到新地址，提醒一下，并准备好新的连接码。
   Util.Thread("relay-code", () => { for (int i = 0; i < 40 && !app.Relay.Ready; i++) Thread.Sleep(250); try { app.Relay.NewCode(); } catch { } });
   app.Feed.Add(new FeedItem { id = Crypto.Token(8), kind = "info", dir = "in", status = "done", note = "临时中转的地址变了。不在同一个网络的电脑要输入新的连接码（添加设备 → 不在同一个网络），手机要重新扫码" });
  }
 }

 /// <summary>隧道开着时盯着：程序退出或连续打不开就重开。</summary>
 void Watch(string url) {
  int bad = 0;
  while (wanted) {
   wake.WaitOne(app.Opt.FakeTunnel ? 1000 : 60000);
   if (!wanted) return;
   if (app.Opt.FakeTunnel) { if (rotate > 0) return; continue; }
   Process p; lock (gate) p = proc;
   if (p == null || p.HasExited) { Log.Write("临时隧道程序退出了，重开"); return; }
   if (Healthy(url, 10000)) { bad = 0; if (State != "ok") Set("ok", null); continue; }
   if (++bad >= 3) { Log.Write("临时隧道连续打不开，重开"); Kill(); return; }
  }
 }

 void Kill() {
  Process p; lock (gate) { p = proc; proc = null; }
  if (p == null) return;
  try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
  try { p.Dispose(); } catch { }
 }

 public void Shutdown() { wanted = false; wake.Set(); Kill(); }

 // ───── 下载 cloudflared ─────
 void Download() {
  Set("download", null); Progress = 0;
  string dir = Path.GetDirectoryName(Tool); Directory.CreateDirectory(dir);
  string part = Tool + ".part";
  string url = DownloadUrl + (Environment.Is64BitOperatingSystem ? "amd64" : "386") + ".exe";
  try {
   var r = Relay.Make(url, "GET", 60000, viaPublic: true); r.AllowAutoRedirect = true; r.ReadWriteTimeout = 60000;
   using (var resp = (HttpWebResponse)r.GetResponse())
   using (var s = resp.GetResponseStream())
   using (var f = File.Create(part)) {
    long total = resp.ContentLength, got = 0, lastBump = 0; var buf = new byte[1 << 16]; int n;
    while ((n = s.Read(buf, 0, buf.Length)) > 0) {
     if (!wanted) throw new ApiError(499, "已取消");
     f.Write(buf, 0, n); got += n;
     if (got > 300L << 20) throw new ApiError(502, "下载的文件不对");
     if (total > 0) Progress = (int)(got * 100 / total);
     if (Util.Ms() - lastBump > 400) { lastBump = Util.Ms(); app.Changes.Bump(); }
    }
    if (total > 0 && got != total) throw new ApiError(502, "下载不完整");
   }
   if (!Signed.ByCloudflare(part)) { try { File.Delete(part); } catch { } throw new ApiError(502, "下载的 cloudflared 没有 Cloudflare 的有效签名，已删除"); }
   if (File.Exists(Tool)) File.Delete(Tool);
   File.Move(part, Tool);
   Log.Write("已下载 cloudflared（" + new FileInfo(Tool).Length / 1048576 + " MB）");
  } catch (ApiError) { throw; }
  catch (Exception e) {
   try { File.Delete(part); } catch { }
   throw new ApiError(502, "下载 Cloudflare 隧道程序失败（" + e.Message + "）。国内访问 GitHub 有时很慢，可以稍后再试；也可以自己下载 cloudflared-windows-amd64.exe，改名 cloudflared.exe 放到 " + Path.GetDirectoryName(Tool));
  }
 }

 /// <summary>校验数字签名：文件没被改过，且签名者是 Cloudflare。</summary>
 static class Signed {
  public static bool ByCloudflare(string path) {
   try {
    if (Verify(path) != 0) return false;
    var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
    return cert.Subject.IndexOf("Cloudflare", StringComparison.OrdinalIgnoreCase) >= 0;
   } catch { return false; }
  }
  static readonly Guid Action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct FileInfo { public int cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
  [StructLayout(LayoutKind.Sequential)]
  struct Data {
   public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
   public IntPtr pFile; public int dwStateAction; public IntPtr hWVTStateData, pwszURLReference; public int dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
  }
  [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);
  static int Verify(string path) {
   var fi = new FileInfo { cbStruct = Marshal.SizeOf(typeof(FileInfo)), pcwszFilePath = path };
   IntPtr pFi = Marshal.AllocHGlobal(Marshal.SizeOf(fi)), pData = IntPtr.Zero;
   try {
    Marshal.StructureToPtr(fi, pFi, false);
    var d = new Data { cbStruct = Marshal.SizeOf(typeof(Data)), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0, dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pFi, dwStateAction = 0, dwProvFlags = 0x1000 /* WTD_CACHE_ONLY_URL_RETRIEVAL */ };
    pData = Marshal.AllocHGlobal(Marshal.SizeOf(d)); Marshal.StructureToPtr(d, pData, false);
    return WinVerifyTrust(IntPtr.Zero, Action, pData);
   } finally { Marshal.FreeHGlobal(pFi); if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData); }
  }
 }

 /// <summary>把子进程放进一个“随我退出”的作业：轻桥崩了或被结束，cloudflared 也跟着退出，不留在后台。</summary>
 static class Job {
  static IntPtr handle;
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
  [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
  [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
  [StructLayout(LayoutKind.Sequential)] struct Basic { public long a, b; public uint LimitFlags; public UIntPtr c, d; public uint e; public UIntPtr f; public uint g, h; }
  [StructLayout(LayoutKind.Sequential)] struct Io { public ulong a, b, c, d, e, f; }
  [StructLayout(LayoutKind.Sequential)] struct Ext { public Basic Basic; public Io Io; public UIntPtr p1, p2, p3, p4; }
  public static void Add(Process p) {
   try {
    lock (typeof(Job)) {
     if (handle == IntPtr.Zero) {
      var h = CreateJobObject(IntPtr.Zero, null);
      var info = new Ext(); info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
      int len = Marshal.SizeOf(typeof(Ext)); var ptr = Marshal.AllocHGlobal(len);
      try { Marshal.StructureToPtr(info, ptr, false); SetInformationJobObject(h, 9 /* ExtendedLimitInformation */, ptr, (uint)len); } finally { Marshal.FreeHGlobal(ptr); }
      handle = h;
     }
     AssignProcessToJobObject(handle, p.Handle);
    }
   } catch (Exception e) { Log.Write("作业对象：" + e.Message); }
  }
 }
}
