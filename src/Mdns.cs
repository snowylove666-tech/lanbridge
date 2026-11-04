using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanBridge;

/// <summary>
/// 极简 mDNS 应答器（沿用 1.5）：让手机用固定名字 lanbridge-xxxxxx.local 找到这台电脑，
/// 电脑换了 IP，手机上存的地址照样能用。
/// </summary>
sealed class Mdns : IDisposable {
 sealed class Adapter { public IPAddress ip; public int index; }
 readonly string host; Socket socket; volatile bool stopped;
 readonly List<Adapter> adapters = new List<Adapter>();
 static readonly IPAddress group = IPAddress.Parse("224.0.0.251");
 public Mdns(string host) { this.host = host; }
 public void Start() {
  try {
   socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
   socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
   socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
   socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
   socket.ReceiveTimeout = 1000; socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
   Refresh();
   Util.Thread("mdns", Listen);
  } catch (Exception e) { Log.Write("mDNS 不可用：" + e.Message); socket?.Close(); socket = null; }
 }
 void Refresh() {
  var current = new List<Adapter>();
  foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
   if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
   try { var p = nic.GetIPProperties(); foreach (var a in p.UnicastAddresses) if (a.Address.AddressFamily == AddressFamily.InterNetwork && Util.IsPrivate(a.Address)) current.Add(new Adapter { ip = a.Address, index = p.GetIPv4Properties().Index }); } catch { }
  }
  foreach (var old in adapters.Where(x => !current.Any(a => a.ip.Equals(x.ip))).ToArray()) { try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(group, old.ip)); } catch { } adapters.Remove(old); }
  foreach (var a in current.Where(x => !adapters.Any(o => o.ip.Equals(x.ip)))) { try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, a.ip)); adapters.Add(a); } catch { } }
 }
 void Listen() {
  DateTime refresh = DateTime.UtcNow; var data = new byte[1500];
  while (!stopped) {
   try {
    if (DateTime.UtcNow - refresh > TimeSpan.FromSeconds(10)) { Refresh(); refresh = DateTime.UtcNow; }
    EndPoint source = new IPEndPoint(IPAddress.Any, 0); SocketFlags flags = SocketFlags.None;
    int n = socket.ReceiveMessageFrom(data, 0, data.Length, ref flags, ref source, out IPPacketInformation info);
    var peer = (IPEndPoint)source; if (!Util.IsPrivate(peer.Address)) continue;
    var adapter = adapters.Find(x => x.index == info.Interface); if (adapter == null) continue;
    var response = Answer(data, n, host, adapter.ip, peer.Port != 5353, out bool unicast);
    if (response == null) continue;
    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, adapter.ip.GetAddressBytes());
    socket.SendTo(response, unicast ? peer : new IPEndPoint(group, 5353));
   } catch (SocketException) { if (stopped) return; } catch (ObjectDisposedException) { return; } catch { }
  }
 }
 static int U16(byte[] b, int i) => b[i] * 256 + b[i + 1];
 static string Name(byte[] b, int size, ref int cursor) {
  int pos = cursor, hops = 0; bool jumped = false; var labels = new List<string>();
  while (pos < size && ++hops < 64) {
   int len = b[pos++];
   if (len == 0) { if (!jumped) cursor = pos; return string.Join(".", labels); }
   if ((len & 192) == 192) { if (pos >= size) break; int target = ((len & 63) << 8) | b[pos++]; if (!jumped) cursor = pos; jumped = true; pos = target; continue; }
   if (len > 63 || pos + len > size) break; labels.Add(Encoding.ASCII.GetString(b, pos, len)); pos += len; if (!jumped) cursor = pos;
  }
  throw new InvalidDataException("mDNS name");
 }
 static void Put16(List<byte> b, int n) { b.Add((byte)(n >> 8)); b.Add((byte)n); }
 static void PutName(List<byte> b, string name) { foreach (var part in name.Split('.')) { var bytes = Encoding.ASCII.GetBytes(part); b.Add((byte)bytes.Length); b.AddRange(bytes); } b.Add(0); }
 public static byte[] Answer(byte[] b, int size, string host, IPAddress ip, bool legacy, out bool unicast) {
  unicast = legacy; if (size < 12 || (b[2] & 128) != 0 || U16(b, 4) > 32) return null;
  int cursor = 12; bool match = false;
  try {
   for (int q = 0; q < U16(b, 4); q++) {
    string name = Name(b, size, ref cursor); if (cursor + 4 > size) return null;
    int type = U16(b, cursor), cls = U16(b, cursor + 2); cursor += 4;
    if (name.Equals(host, StringComparison.OrdinalIgnoreCase) && (type == 1 || type == 255) && (cls & 32767) == 1) { match = true; if ((cls & 32768) != 0) unicast = true; }
   }
  } catch { return null; }
  if (!match) return null;
  var r = new List<byte>(128); Put16(r, legacy ? U16(b, 0) : 0); Put16(r, 0x8400); Put16(r, legacy ? U16(b, 4) : 0); Put16(r, 1); Put16(r, 0); Put16(r, 0);
  if (legacy) r.AddRange(b.Take(cursor).Skip(12));
  PutName(r, host); Put16(r, 1); Put16(r, legacy ? 1 : 0x8001); r.AddRange(new byte[] { 0, 0, 0, (byte)(legacy ? 10 : 120) }); Put16(r, 4); r.AddRange(ip.GetAddressBytes()); return r.ToArray();
 }
 public void Dispose() { stopped = true; socket?.Close(); }
}
