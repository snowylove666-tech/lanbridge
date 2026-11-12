using System;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace LanBridge;

static class Program {
 public const string Version = "2.2.18";

 [STAThread]
 static int Main(string[] args) {
  Embedded.Hook();
  if (args.Length == 2 && args[0] == "--make-icon") { Brand.WriteIco(args[1]); return 0; }
  // 服务器上只当常驻中转 + 小云文件池。
  if (Array.IndexOf(args, "--relay-server") >= 0) return RelayHost.Run(args);
  var o = Options.Parse(args);
  // 守护任务每 5 分钟来看一眼：轻桥没在运行就把它开起来；是用户自己点“退出”的就不打扰。
  if (o.Guard && File.Exists(Path.Combine(o.RootDir, "quit.flag"))) return 0;
  using var mutex = new Mutex(false, "Local\\LanBridge2-" + o.Port);
  bool owned; try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
  if (!owned) {
   // 已经在运行：把“打开窗口 / 发送这些文件”转给它。
   CommandPipe.Send(o.Port, o.Send != null ? "send" : (o.Background ? "noop" : "show"), o.Send ?? new string[0]);
   return 0;
  }
  Application.EnableVisualStyles();
  Application.SetCompatibleTextRenderingDefault(false);
  ServicePointManager.DefaultConnectionLimit = 64;
  ServicePointManager.Expect100Continue = false;
  ServicePointManager.UseNagleAlgorithm = false;
  // 出了没料到的错：记下来（以前悄悄没了，查不出原因）。界面线程上的错不让它带走整个程序。
  Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
  Application.ThreadException += (s, e) => Log.Write("界面出错（已忽略）：" + e.Exception);
  AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("意外出错，轻桥要退出了：" + e.ExceptionObject);
  App app;
  try { app = new App(o); }
  catch (Exception e) {
   if (!o.Headless) MessageBox.Show("轻桥没能启动：\n\n" + e.Message, "轻桥", MessageBoxButtons.OK, MessageBoxIcon.Warning);
   return 1;
  }
  if (!o.Guard) try { File.Delete(Path.Combine(o.RootDir, "quit.flag")); } catch { }
  else Log.Write("轻桥之前没在运行，守护任务把它重新打开了");
  SystemEvents.SessionEnding += (s, e) => Log.Write(e.Reason == SessionEndReasons.Logoff ? "系统注销" : "系统关机 / 重启");
  SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == PowerModes.Suspend) Log.Write("电脑睡眠（睡着时手机连不到这台电脑）"); else if (e.Mode == PowerModes.Resume) Log.Write("电脑唤醒"); };
  Application.Run(app);
  Log.Write(app.UserExit ? "轻桥退出（手动点了退出）" : "轻桥退出（没有点退出：关机、注销，或被别的程序结束）");
  app.Shutdown();
  return 0;
 }
}
