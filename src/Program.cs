using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace LanBridge;

static class Program {
 public const string Version = "2.2.5";

 [STAThread]
 static int Main(string[] args) {
  Embedded.Hook();
  if (args.Length == 2 && args[0] == "--make-icon") { Brand.WriteIco(args[1]); return 0; }
  // 服务器上只当常驻中转 + 小云文件池。
  if (Array.IndexOf(args, "--relay-server") >= 0) return RelayHost.Run(args);
  var o = Options.Parse(args);
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
  App app;
  try { app = new App(o); }
  catch (Exception e) {
   if (!o.Headless) MessageBox.Show("轻桥没能启动：\n\n" + e.Message, "轻桥", MessageBoxButtons.OK, MessageBoxIcon.Warning);
   return 1;
  }
  Application.Run(app);
  app.Shutdown();
  return 0;
 }
}
