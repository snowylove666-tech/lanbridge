using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanBridge {
 public sealed class NearbyComputer { public string id,name,url; public override string ToString() { return name+"  ·  "+url; } }
 public sealed class ComputerDiscovery : IDisposable {
  const int Port=49321; const string Probe="LanBridge discovery 1";
  static readonly IPAddress group=IPAddress.Parse("239.255.49.21");
  readonly BridgeState state; Socket socket; volatile bool stopped;
  public ComputerDiscovery(BridgeState state) { this.state=state; }
  public void Start() {
   try {
    socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp); socket.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);
    socket.ReceiveTimeout=1500; socket.Bind(new IPEndPoint(IPAddress.Any,Port)); JoinInterfaces();
    new Thread(Listen) {IsBackground=true,Name="Nearby computers"}.Start();
   } catch { if(socket!=null) socket.Close(); }
  }
  void JoinInterfaces() { foreach(string a in BridgeState.Addresses()) try { socket.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.AddMembership,new MulticastOption(group,IPAddress.Parse(a))); } catch {} }
  void Listen() {
   byte[] data=new byte[512]; DateTime refresh=DateTime.UtcNow;
   while(!stopped) try {
    if(DateTime.UtcNow-refresh>TimeSpan.FromSeconds(15)) { JoinInterfaces(); refresh=DateTime.UtcNow; }
    EndPoint source=new IPEndPoint(IPAddress.Any,0); int n=socket.ReceiveFrom(data,ref source); var peer=(IPEndPoint)source;
    if(!BridgeState.PrivateAddress(peer.Address) || Encoding.ASCII.GetString(data,0,n)!=Probe) continue;
    byte[] reply=Encoding.UTF8.GetBytes(BridgeState.Json().Serialize(new { app="LanBridge",id=state.Prefs.installId,name=Environment.MachineName,port=state.Port })); socket.SendTo(reply,source);
   } catch(SocketException) { if(stopped) return; } catch(ObjectDisposedException) { return; } catch {}
  }
  public static NearbyComputer[] Scan(string excludeId) {
   var found=new Dictionary<string,NearbyComputer>();
   using(var udp=new UdpClient(AddressFamily.InterNetwork)) {
    udp.Client.ReceiveTimeout=300; udp.Client.Bind(new IPEndPoint(IPAddress.Any,0)); udp.Client.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.MulticastTimeToLive,1);
    byte[] request=Encoding.ASCII.GetBytes(Probe);
    foreach(string address in BridgeState.Addresses()) try { udp.Client.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.MulticastInterface,IPAddress.Parse(address).GetAddressBytes()); udp.Send(request,request.Length,new IPEndPoint(group,Port)); } catch {}
    DateTime until=DateTime.UtcNow.AddMilliseconds(1800);
    while(DateTime.UtcNow<until) try {
     var peer=new IPEndPoint(IPAddress.Any,0); byte[] bytes=udp.Receive(ref peer); if(bytes.Length>1024 || !BridgeState.PrivateAddress(peer.Address)) continue;
     var d=BridgeState.Json().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(bytes));
     string id=Request.Value(d,"id"); object p;
     if(Request.Value(d,"app")!="LanBridge" || id==excludeId || id.Length!=32 || !d.TryGetValue("port",out p)) continue;
     int port=Convert.ToInt32(p); if(port<1024 || port>65535) continue;
     found[id]=new NearbyComputer {id=id,name=Request.Value(d,"name"),url="http://"+peer.Address+":"+port};
    } catch(SocketException) {} catch {}
   }
   return found.Values.OrderBy(x=>x.name).ToArray();
  }
  public void Dispose() { stopped=true; if(socket!=null) socket.Close(); }
 }
}
