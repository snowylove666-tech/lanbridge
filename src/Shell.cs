using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LanBridge;

/// <summary>和 Windows 打交道的小事：下载文件夹、开机启动、右键“发送到”、防火墙、资源管理器。</summary>
static class Shell {
 [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
 static readonly Guid Downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

 public static string ExePath => Assembly.GetExecutingAssembly().Location;

 public static string DefaultReceiveFolder() {
  string root = null;
  try { if (SHGetKnownFolderPath(Downloads, 0, IntPtr.Zero, out var p) == 0) { root = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); } } catch { }
  if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
  return Path.Combine(root, "轻桥");
 }

 static string form;
 /// <summary>有电池就当笔记本，图标用笔记本。</summary>
 public static string Form => form ??= SystemInformation.PowerStatus.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) ? "desktop" : "laptop";

 public static void Reveal(string path) {
  try { if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\""); else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\""); } catch { }
 }
 public static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { Reveal(path); } }
 public static void OpenFolder(string dir) { try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); } catch { } }

 public static string ZipFolder(string dir, string stage) {
  Directory.CreateDirectory(stage);
  string name = Path.GetFileName(dir.TrimEnd('\\', '/')); if (name.Length == 0) name = "文件夹";
  string zip = Util.Reserve(stage, Util.SafeName(name) + ".zip");
  try { if (File.Exists(zip)) File.Delete(zip); ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Fastest, true); }
  finally { Util.Release(zip); }
  return zip;
 }

 public static void CleanOld(string dir, TimeSpan age, string keep) {
  try {
   if (!Directory.Exists(dir)) return;
   foreach (var d in Directory.GetDirectories(dir)) if (Path.GetFileName(d) != keep && Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow - age) try { Directory.Delete(d, true); } catch { }
   foreach (var f in Directory.GetFiles(dir)) if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow - age) try { File.Delete(f); } catch { }
  } catch { }
 }

 const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
 public static void SetAutostart(bool on) {
  try {
   using var k = Registry.CurrentUser.CreateSubKey(RunKey);
   if (on) k.SetValue("LanBridge", "\"" + ExePath + "\" --background"); else k.DeleteValue("LanBridge", false);
  } catch (Exception e) { Log.Write("开机启动设置失败：" + e.Message); }
  SetGuard(on);
 }

 /// <summary>
 /// 守护任务：每 5 分钟看一眼，轻桥没在运行就重新打开（它要是没了，手机就连不到这台电脑）。
 /// 已经在运行时新进程马上退出；用户自己点“退出”的不打扰。只属于当前用户，不需要管理员。
 /// </summary>
 const string GuardTask = "LanBridge Guard";
 static bool? guardOn;
 public static void SetGuard(bool on) {
  try {
   if (guardOn == on) return;
   if (!on) { Sch("/delete /f /tn \"" + GuardTask + "\""); guardOn = false; return; }
   string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
    + "<RegistrationInfo><Description>轻桥意外退出后自动重新打开（跟着轻桥设置里的“开机自动启动”开关）</Description></RegistrationInfo>"
    + "<Triggers><TimeTrigger><Repetition><Interval>PT5M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>2026-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>"
    + "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"
    + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority><Enabled>true</Enabled></Settings>"
    + "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(ExePath) + "</Command><Arguments>--background --guard</Arguments></Exec></Actions></Task>";
   string f = Path.Combine(Path.GetTempPath(), "lanbridge-guard-" + Process.GetCurrentProcess().Id + ".xml");
   File.WriteAllText(f, xml, Encoding.Unicode);
   try { int code = Sch("/create /f /tn \"" + GuardTask + "\" /xml \"" + f + "\""); if (code != 0) Log.Write("守护任务没建成（代码 " + code + "）"); else guardOn = true; }
   finally { try { File.Delete(f); } catch { } }
  } catch (Exception e) { Log.Write("守护任务设置失败：" + e.Message); }
 }
 static int Sch(string args) {
  using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false });
  return p.WaitForExit(15000) ? p.ExitCode : -1;
 }

 static string SendToLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "轻桥.lnk");
 public static void SetSendTo(bool on) {
  try {
   foreach (var old in new[] { "轻桥 · 发到 iPhone.lnk" }) { var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), old); if (File.Exists(p)) File.Delete(p); }
   // 先删再建：换了图标后 Windows 和别的软件按文件时间认新图标
   if (File.Exists(SendToLink)) File.Delete(SendToLink);
   if (!on) return;
   var t = Type.GetTypeFromProgID("WScript.Shell"); object sh = Activator.CreateInstance(t);
   object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { SendToLink });
   var lt = link.GetType();
   lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { ExePath });
   lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { "--send" });
   lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { ExePath + ",0" });
   lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "用轻桥发送到其他设备" });
   lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
   Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(sh);
  } catch (Exception e) { Log.Write("“发送到”设置失败：" + e.Message); }
 }
 [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);
 /// <summary>告诉资源管理器图标变了（重建图标缓存里的这一项）。</summary>
 public static void IconsChanged() { try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { } }

 /// <summary>别的设备连不进来时用：弹一次管理员确认，放行轻桥（所有网络类型，包括被 Windows 标成“公用”的家庭 Wi-Fi）。</summary>
 public static bool AllowFirewall() {
  string exe = ExePath;
  string args = "/c netsh advfirewall firewall delete rule name=\"轻桥 LanBridge\" >nul 2>&1 & " +
   "netsh advfirewall firewall add rule name=\"轻桥 LanBridge\" dir=in action=allow program=\"" + exe + "\" enable=yes profile=any >nul";
  try {
   var p = Process.Start(new ProcessStartInfo("cmd.exe", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
   p?.WaitForExit(15000); return p != null && p.ExitCode == 0;
  } catch { return false; }
 }

 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
 public static bool DarkMode() {
  try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return k?.GetValue("AppsUseLightTheme") is int v && v == 0; } catch { return false; }
 }
 /// <summary>Windows 11 标题栏颜色跟页面一致。</summary>
 public static void TitleBar(IntPtr hwnd, bool dark, System.Drawing.Color caption) {
  try {
   int d = dark ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref d, 4);
   int c = caption.R | (caption.G << 8) | (caption.B << 16); DwmSetWindowAttribute(hwnd, 35, ref c, 4);
  } catch { }
 }
}

/// <summary>第二次启动（比如右键“发送到 → 轻桥”）把参数转给已经在运行的那个。</summary>
sealed class CommandPipe : IDisposable {
 readonly string name; readonly Action<string, string[]> received; volatile bool running = true;
 static string NameFor(int port) => "LanBridge2-" + port + "-" + WindowsIdentity.GetCurrent().User.Value;
 public CommandPipe(int port, Action<string, string[]> received) { name = NameFor(port); this.received = received; Util.Thread("pipe", Listen); }
 void Listen() {
  while (running) {
   try {
    var sec = new PipeSecurity(); sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
    using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, sec);
    pipe.WaitForConnection();
    var head = new byte[4]; Util.ReadExact(pipe, head, 0, 4); int len = BitConverter.ToInt32(head, 0); if (len < 2 || len > 256 * 1024) continue;
    var b = new byte[len]; Util.ReadExact(pipe, b, 0, len);
    var d = J.Obj(b); received(d.Get("cmd"), d.List("files").ToArray());
   } catch { if (!running) return; Thread.Sleep(200); }
  }
 }
 public static bool Send(int port, string cmd, string[] files) {
  try {
   using var c = new NamedPipeClientStream(".", NameFor(port), PipeDirection.Out); c.Connect(2500);
   var b = J.Bytes(new { cmd, files }); c.Write(BitConverter.GetBytes(b.Length), 0, 4); c.Write(b, 0, b.Length); c.Flush(); return true;
  } catch { return false; }
 }
 public void Dispose() { running = false; try { using var c = new NamedPipeClientStream(".", name, PipeDirection.Out); c.Connect(200); } catch { } }
}

/// <summary>WebView2 的 DLL 打包在 EXE 里，用到时才加载；原生加载器释放到本地数据目录。</summary>
static class Embedded {
 public static void Hook() => AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
  string n = new AssemblyName(e.Name).Name;
  if (n != "Microsoft.Web.WebView2.Core" && n != "Microsoft.Web.WebView2.WinForms") return null;
  using var st = typeof(Embedded).Assembly.GetManifestResourceStream("lib/" + n + ".dll");
  if (st == null) return null;
  using var ms = new MemoryStream(); st.CopyTo(ms); return Assembly.Load(ms.ToArray());
 };
 public static byte[] Resource(string name) {
  using var st = typeof(Embedded).Assembly.GetManifestResourceStream(name);
  if (st == null) return null; using var ms = new MemoryStream(); st.CopyTo(ms); return ms.ToArray();
 }
 public static string LoaderFolder(string root) {
  string arch = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "arm64", Architecture.X86 => "x86", _ => "x64" };
  string dir = Path.Combine(root, "runtime", "wv2-1.0.3650.58-" + arch), file = Path.Combine(dir, "WebView2Loader.dll");
  if (!File.Exists(file)) {
   Directory.CreateDirectory(dir);
   var b = Resource("lib/" + arch + "/WebView2Loader.dll"); string tmp = file + "." + Crypto.Token(4);
   File.WriteAllBytes(tmp, b); try { File.Move(tmp, file); } catch { try { File.Delete(tmp); } catch { } }
  }
  return dir;
 }
}
