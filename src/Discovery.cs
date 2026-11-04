using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LanBridge;

/// <summary>
/// 自动找电脑，三条路一起走：
/// 1. UDP 广播 + 组播（同一个路由器下几乎都能用）；
/// 2. 扫描本网段（有的路由器拦广播，比如有线和无线之间）；
/// 3. 家里两级路由时，顺带扫一下上一级路由器的网段（从后面那台能连到前面那台）。
/// </summary>
sealed class Discovery {
 public const int UdpPort = 49321;
 static readonly IPAddress Group = IPAddress.Parse("239.255.49.21");
 readonly App app; UdpClient udp; volatile bool running = true;
 readonly AutoResetEvent kickScan = new AutoResetEvent(false), kickBeacon = new AutoResetEvent(false);
 public volatile bool Scanning; public long LastScan; public string Hop;
 int burst = 4;

 public Discovery(App app) { this.app = app; }

 public void Start() {
  if (!app.Opt.Loopback) {
   try {
    udp = new UdpClient(AddressFamily.InterNetwork);
    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
    udp.EnableBroadcast = true;
    JoinGroups();
    Util.Thread("beacon-in", Listen);
    Util.Thread("beacon-out", BeaconLoop);
   } catch (Exception e) { Log.Write("广播不可用：" + e.Message); }
   NetworkChange.NetworkAddressChanged += (s, e) => { burst = 4; Kick(); };
  }
  Util.Thread("scan", ScanLoop);
 }
 public void Stop() { running = false; try { udp?.Close(); } catch { } kickScan.Set(); kickBeacon.Set(); }
 public void Kick() { kickScan.Set(); kickBeacon.Set(); }

 void JoinGroups() {
  foreach (var a in Util.LocalAddrs()) try { udp.JoinMulticastGroup(Group, a.Ip); } catch { }
 }

 byte[] Beacon(bool hi) {
  var h = (Dictionary<string, object>)J.Obj(J.Str(app.Hub.PublicHello()));
  if (hi) h["hi"] = true;
  return J.Bytes(h);
 }

 void BeaconLoop() {
  while (running) {
   bool hi = burst > 0; if (burst > 0) burst--;
   Send(Beacon(hi));
   kickBeacon.WaitOne(hi ? 1500 : 8000);
  }
 }

 void Send(byte[] b) {
  if (udp == null) return;
  try { udp.Send(b, b.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort)); } catch { }
  foreach (var a in Util.LocalAddrs().Where(x => x.Score > -10)) {
   try {
    var ip = a.Ip.GetAddressBytes(); var m = a.Mask.GetAddressBytes(); var bc = new byte[4];
    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~m[i]);
    udp.Send(b, b.Length, new IPEndPoint(new IPAddress(bc), UdpPort));
   } catch { }
   try {
    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.Ip.GetAddressBytes());
    udp.Send(b, b.Length, new IPEndPoint(Group, UdpPort));
   } catch { }
  }
 }

 void Listen() {
  while (running) {
   try {
    IPEndPoint ep = null; var data = udp.Receive(ref ep);
    if (!Util.IsPrivate(ep.Address) || data.Length > 4096) continue;
    var d = J.Obj(data);
    if (d.Num("lb") != 2 || d.Get("id") == app.C.deviceId) continue;
    bool fresh = app.Hub.Seen(d, ep.Address.ToString());
    // 新开的轻桥会连发几次“hi”，单独回它一次，让它立刻看到我们。
    if (d.Flag("hi") == true || fresh) { var b = Beacon(false); udp.Send(b, b.Length, new IPEndPoint(ep.Address, UdpPort)); }
   } catch (ObjectDisposedException) { return; } catch (SocketException) { if (!running) return; } catch { }
  }
 }

 void ScanLoop() {
  Thread.Sleep(800);
  while (running) {
   try { Scan(); } catch (Exception e) { Log.Write("扫描出错：" + e.Message); }
   // 组里还有电脑没连上时扫得勤一些。
   bool missing = app.Hub.Links().Any(l => l.State == "offline");
   kickScan.WaitOne(missing ? 60000 : 180000);
  }
 }

 public void Rescan() { kickScan.Set(); burst = 3; kickBeacon.Set(); }

 void Scan() {
  Scanning = true; app.Changes.Bump();
  var targets = new HashSet<string>();
  var ports = new HashSet<int> { 49321, app.Opt.Port };
  foreach (var s in app.Opt.Scan) targets.Add(s);
  if (!app.Opt.Loopback) {
   var addrs = Util.LocalAddrs().Where(a => a.Score >= 0).ToList();
   var mine = new HashSet<string>(addrs.Select(a => a.Ip.ToString()));
   var nets = new HashSet<string>();
   foreach (var a in addrs) {
    int prefix = a.Mask.GetAddressBytes().Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
    if (prefix < 16 || prefix > 30) continue;
    var b = a.Ip.GetAddressBytes(); nets.Add(b[0] + "." + b[1] + "." + b[2]);
   }
   Hop = Hop2();
   if (Hop != null) { var b = IPAddress.Parse(Hop).GetAddressBytes(); nets.Add(b[0] + "." + b[1] + "." + b[2]); }
   foreach (var n in nets.Take(4)) for (int i = 1; i < 255; i++) { string ip = n + "." + i; if (!mine.Contains(ip)) foreach (var p in ports) targets.Add(ip + ":" + p); }
  }
  foreach (var l in app.Hub.Links()) lock (app.Gate) foreach (var a in l.M.addrs) targets.Add(a + ":" + l.M.port);
  Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 64 }, t => {
   if (!running) return;
   int c = t.LastIndexOf(':'); string ip = t.Substring(0, c); int port = int.Parse(t.Substring(c + 1));
   if (!Client.CanConnect(ip, port, 450)) return;
   try {
    var d = J.Obj(Client.Send(Client.Make("http://" + ip + ":" + port + "/lb/hello", "GET", 2000), null));
    if (d.Num("lb") == 2 && d.Get("id") != app.C.deviceId) app.Hub.Seen(d, ip);
   } catch { }
  });
  Scanning = false; LastScan = Util.Ms(); app.Changes.Bump();
 }

 /// <summary>第二跳路由器的地址：家里光猫 + 路由器两级时，就是光猫那一层。</summary>
 static string Hop2() {
  foreach (var target in new[] { "223.5.5.5", "8.8.8.8" }) {
   try {
    using var p = new Ping();
    var r = p.Send(target, 700, new byte[8], new PingOptions(2, true));
    if (r != null && r.Status == IPStatus.TtlExpired && Util.IsPrivate(r.Address) && r.Address.GetAddressBytes()[0] != 100) return r.Address.ToString();
    if (r != null && r.Status == IPStatus.TtlExpired) return null;
   } catch { }
  }
  return null;
 }

 /// <summary>30 秒没再出现的“附近电脑”从列表里拿掉。</summary>
 public void Expire() {
  bool changed = false;
  lock (app.Gate) {
   long now = Util.Ms();
   foreach (var n in app.Hub.Near.Values.Where(n => now - n.seen > 45000 && n.state != "asking" && n.state != "waiting").ToList()) { app.Hub.Near.Remove(n.id); changed = true; }
   changed |= app.Hub.Requests.RemoveAll(r => r.until < now) > 0;
  }
  if (changed) app.Changes.Bump();
 }
}
