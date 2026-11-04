using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LanBridge {
 public sealed class DesktopCommands : IDisposable {
  readonly string name; readonly Action<string[]> received;
  volatile bool running=true; NamedPipeServerStream pipe;
  public DesktopCommands(int port,Action<string[]> received) {
   name="LanBridge-"+port+"-"+WindowsIdentity.GetCurrent().User.Value; this.received=received;
   new Thread(Listen) { IsBackground=true,Name="Desktop commands" }.Start();
  }
  void Listen() {
   while(running) {
    try {
     var security=new PipeSecurity(); security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
     using(var next=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security)) {
      pipe=next; if(!running) return; next.WaitForConnection();
      byte[] header=new byte[4]; Exact(next,header); int length=BitConverter.ToInt32(header,0); if(length<2 || length>65536) continue;
      byte[] b=new byte[length]; Exact(next,b); var paths=BridgeState.Json().Deserialize<string[]>(Encoding.UTF8.GetString(b));
      if(paths!=null && paths.Length<=100) received(paths);
     }
    } catch { if(!running) return; }
   }
  }
  static void Exact(Stream stream,byte[] bytes) { int offset=0; while(offset<bytes.Length) { Task<int> read=stream.ReadAsync(bytes,offset,bytes.Length-offset); if(!read.Wait(3000)) throw new IOException("命令超时"); int n=read.Result; if(n<=0) throw new EndOfStreamException(); offset+=n; } }
  public static bool Send(int port,string[] paths) {
   string name="LanBridge-"+port+"-"+WindowsIdentity.GetCurrent().User.Value;
   try { using(var client=new NamedPipeClientStream(".",name,PipeDirection.Out)) { client.Connect(1800); byte[] b=Encoding.UTF8.GetBytes(BridgeState.Json().Serialize(paths)); if(b.Length>65536) return false; byte[] size=BitConverter.GetBytes(b.Length); client.Write(size,0,size.Length); client.Write(b,0,b.Length); client.Flush(); } return true; } catch { return false; }
  }
  public static void InstallSendTo(string executable,int port,string folder=null) {
   if(folder==null) folder=Environment.GetFolderPath(Environment.SpecialFolder.SendTo); string path=Path.Combine(folder,"轻桥 · 发到 iPhone.lnk");
   Type type=Type.GetTypeFromProgID("WScript.Shell"); object shell=Activator.CreateInstance(type); object link=type.InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,shell,new object[]{path});
   Type lt=link.GetType(); lt.InvokeMember("TargetPath",System.Reflection.BindingFlags.SetProperty,null,link,new object[]{executable}); lt.InvokeMember("Arguments",System.Reflection.BindingFlags.SetProperty,null,link,new object[]{"--port "+port+" --send"}); lt.InvokeMember("WorkingDirectory",System.Reflection.BindingFlags.SetProperty,null,link,new object[]{Path.GetDirectoryName(executable)}); lt.InvokeMember("Save",System.Reflection.BindingFlags.InvokeMethod,null,link,null);
   System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
  }
  public void Dispose() { running=false; try { if(pipe!=null) pipe.Dispose(); } catch {} }
 }
}
