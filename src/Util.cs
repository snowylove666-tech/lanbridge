using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LanBridge;

public sealed class ApiError : Exception {
 public readonly int Status;
 public ApiError(int status, string message) : base(message) { Status = status; }
}

static class J {
 public static JavaScriptSerializer S() => new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 64 };
 public static string Str(object o) => S().Serialize(o);
 public static byte[] Bytes(object o) => Encoding.UTF8.GetBytes(Str(o));
 public static T Parse<T>(string s) => S().Deserialize<T>(s);
 public static T Parse<T>(byte[] b) => S().Deserialize<T>(Encoding.UTF8.GetString(b));
 public static Dictionary<string, object> Obj(string s) => S().Deserialize<Dictionary<string, object>>(s) ?? new Dictionary<string, object>();
 public static Dictionary<string, object> Obj(byte[] b) => Obj(Encoding.UTF8.GetString(b));
 public static string Get(this Dictionary<string, object> d, string k) =>
  d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
 public static bool? Flag(this Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is bool b ? b : (bool?)null;
 public static long Num(this Dictionary<string, object> d, string k, long fallback = 0) =>
  d != null && d.TryGetValue(k, out var v) && v != null && long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
 public static List<string> List(this Dictionary<string, object> d, string k) {
  var r = new List<string>();
  if (d != null && d.TryGetValue(k, out var v) && v is System.Collections.IEnumerable e && !(v is string))
   foreach (var x in e) if (x != null) r.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
  return r;
 }
}

sealed class LocalAddr {
 public IPAddress Ip, Mask; public bool Gateway; public string Name; public int Score;
 public override string ToString() => Ip.ToString();
}

static class Util {
 public static long Ms() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

 /// <summary>私有地址，外加 100.64/10（Tailscale 等虚拟局域网也能直接用）。</summary>
 public static bool IsPrivate(IPAddress ip) {
  if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false;
  var b = ip.GetAddressBytes();
  return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
   || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
 }

 static readonly string[] VirtualWords = { "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "tap-", "wintun", "wsl", "clash", "meta", "docker", "loopback", "bluetooth", "vpn", "npcap" };

 /// <summary>本机可用的 IPv4，按“像真实网卡”的程度排序：有网关、Wi-Fi/有线优先，虚拟网卡靠后。</summary>
 public static List<LocalAddr> LocalAddrs() {
  var list = new List<LocalAddr>();
  NetworkInterface[] nics;
  try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { return list; }
  foreach (var ni in nics) {
   if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
   IPInterfaceProperties p; try { p = ni.GetIPProperties(); } catch { continue; }
   bool gw = p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
   string label = (ni.Name + " " + ni.Description).ToLowerInvariant();
   bool virt = VirtualWords.Any(w => label.Contains(w));
   foreach (var a in p.UnicastAddresses) {
    if (a.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(a.Address) || IPAddress.IsLoopback(a.Address)) continue;
    int score = (gw ? 10 : 0) + (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 5 : 0) - (virt ? 20 : 0);
    if (a.Address.GetAddressBytes()[0] == 169) score -= 30;
    list.Add(new LocalAddr { Ip = a.Address, Mask = a.IPv4Mask ?? IPAddress.Parse("255.255.255.0"), Gateway = gw, Name = ni.Name, Score = score });
   }
  }
  return list.GroupBy(x => x.Ip.ToString()).Select(g => g.First()).OrderByDescending(x => x.Score).ToList();
 }

 public static string PrimaryIp() => LocalAddrs().FirstOrDefault()?.Ip.ToString() ?? "127.0.0.1";

 public static string SafeName(string s) {
  s = (s ?? "file").Replace('\\', '/');
  s = s.Substring(s.LastIndexOf('/') + 1);
  foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
  s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim().TrimEnd('.', ' ');
  if (s.Length == 0) s = "file";
  if (s.Length > 150) { string ext = Path.GetExtension(s); if (ext.Length > 20) ext = ""; s = s.Substring(0, 150 - ext.Length) + ext; }
  string stem = Path.GetFileNameWithoutExtension(s).ToUpperInvariant();
  if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) s = "_" + s;
  return s;
 }

 static readonly object uniqueGate = new object();
 static readonly HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 /// <summary>在文件夹里给文件找一个不冲突的名字：“报告.pdf”→“报告 (2).pdf”。名字会被占住直到 Release。</summary>
 public static string Reserve(string dir, string name) {
  lock (uniqueGate) {
   string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
   for (int i = 1; ; i++) {
    string p = Path.Combine(dir, i == 1 ? name : stem + " (" + i + ")" + ext);
    if (!File.Exists(p) && !Directory.Exists(p) && !File.Exists(p + ".lbpart") && reserved.Add(p)) return p;
   }
  }
 }
 public static void Release(string path) { lock (uniqueGate) reserved.Remove(path); }

 public static string Size(long n) => n < 1024 ? n + " B" : n < 1048576 ? (n / 1024.0).ToString("0.#") + " KB" : n < 1073741824 ? (n / 1048576.0).ToString("0.#") + " MB" : (n / 1073741824.0).ToString("0.##") + " GB";

 public static void ReadExact(Stream s, byte[] b, int off, int n) {
  while (n > 0) { int r = s.Read(b, off, n); if (r <= 0) throw new IOException("连接中断"); off += r; n -= r; }
 }

 public static string Clip(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";

 public static void Thread(string name, ThreadStart body) {
  new System.Threading.Thread(() => { try { body(); } catch (Exception ex) { Log.Write(name + " 退出：" + ex); } }) { IsBackground = true, Name = name }.Start();
 }
}

static class Log {
 static string file; static readonly object gate = new object();
 public static void Init(string root) { file = Path.Combine(root, "lanbridge.log"); try { if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Delete(file); } catch { } }
 public static void Write(string line) {
  if (file == null) return;
  lock (gate) try { File.AppendAllText(file, DateTime.Now.ToString("MM-dd HH:mm:ss ") + line + Environment.NewLine, Encoding.UTF8); } catch { }
 }
}

/// <summary>界面状态的版本号。任何会改变界面的事都 Bump 一下，长轮询据此返回。</summary>
sealed class Changes {
 long rev = 1; readonly object g = new object();
 public long Rev { get { lock (g) return rev; } }
 public void Bump() { lock (g) { rev++; Monitor.PulseAll(g); } }
 public long Wait(long since, int ms) {
  int end = Environment.TickCount + ms;
  lock (g) {
   while (rev == since) { int left = end - Environment.TickCount; if (left <= 0) break; Monitor.Wait(g, left); }
   return rev;
  }
 }
}
