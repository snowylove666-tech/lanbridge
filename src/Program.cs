using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LanBridge {
 static class Program {
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [STAThread] static void Main(string[] args) {
   bool test=args.Contains("--test-mode");
   string root=AppDomain.CurrentDomain.BaseDirectory; int port=49321;
   string[] sendFiles=null;
   for(int i=0;i<args.Length;i++) { if(args[i]=="--data" && i+1<args.Length) root=Path.GetFullPath(args[++i]); else if(args[i]=="--port" && i+1<args.Length) port=Int32.Parse(args[++i]); else if(args[i]=="--send") { sendFiles=args.Skip(i+1).ToArray(); break; } }
   if(port<1024 || port>65535) return;
   using(var mutex=new Mutex(false,"Local\\LanBridge-"+port)) {
    if(!mutex.WaitOne(0)) { if(!DesktopCommands.Send(port,sendFiles??new string[0]) && !test) MessageBox.Show("轻桥正在启动，请稍后重试或双击托盘图标。","轻桥"); return; }
    try {
     SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
     var state=new BridgeState(root,port); string testKey=test?BridgeState.RandomToken(32):null;
     if(test && args.Contains("--preview-only")) { using(var previewPeer=new PeerClient(state)) using(var preview=new BridgeForm(state,true)) Application.Run(preview); return; }
     using(var form=new BridgeForm(state,test)) using(var server=new BridgeServer(state,testKey)) using(var discovery=new LocalDiscovery(state)) using(var remote=new RemoteConnection(state)) using(var peer=new PeerClient(state)) using(var computers=new ComputerDiscovery(state)) {
      // Create the STA dispatch handle before accepting clipboard requests.
      var handle=form.Handle; server.Start(); discovery.Start(); computers.Start(); peer.Resume();
      using(var isolatedRemote=test && args.Contains("--test-remote")?new BridgeServer(state,null,true):null) using(var commands=new DesktopCommands(port,form.AcceptCommand)) {
      if(isolatedRemote!=null) { state.RemoteOrigin="https://test-only.invalid"; state.RemoteUntil=DateTime.UtcNow.AddMinutes(5); state.RemoteStatus="隔离测试入口（非真实跨网通道）"; isolatedRemote.Start(); }
      if(sendFiles!=null) form.Shown+=delegate { form.AcceptCommand(sendFiles); };
      if(test) {
       File.WriteAllText(Path.Combine(root,"Data","test-bootstrap.json"),BridgeState.Json().Serialize(new { code=state.PairCode,pair=state.PairSecret,key=testKey,port,host=state.LocalHost,remotePort=isolatedRemote==null?0:isolatedRemote.ListeningPort }));
       using(var qr=PairQr.Render("http://127.0.0.1:"+port+"/#pair="+state.PairSecret)) qr.Save(Path.Combine(root,"Data","test-qr.png"));
       using(var qr=PairQr.Render("https://example-long-temporary-name.trycloudflare.com/#pair="+state.PairSecret)) qr.Save(Path.Combine(root,"Data","test-qr-long.png"));
       if(args.Contains("--show-test-ui")) Application.Run(form); else Application.Run();
      } else { if(args.Contains("--background") || sendFiles!=null) form.Shown+=delegate { form.Hide(); }; Application.Run(form); }
      }
     }
    } catch(Exception ex) { if(test) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"test-error.txt"),ex.ToString()); } else MessageBox.Show("启动失败："+ex.Message+"\n\n若端口被占用，可从命令行指定 --port 49322。","轻桥",MessageBoxButtons.OK,MessageBoxIcon.Error); }
    finally { mutex.ReleaseMutex(); }
   }
  }
 }
}
