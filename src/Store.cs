using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LanBridge;

/// <summary>同一设备组里的另一台电脑。</summary>
public sealed class Member {
 public string id, name, form; public int port = 49321; public List<string> addrs = new List<string>(); public long seen, added;
}

/// <summary>连过这台电脑的手机（Safari 网页 / 快捷指令）。只存令牌的哈希。</summary>
public sealed class Phone {
 public string id, name, hash; public long added, seen;
 /// <summary>同一部手机会从好几个地方打开轻桥（Safari、主屏幕图标、微信里、家里地址和中转地址），各有各的授权；都记在这一张卡片上。</summary>
 public List<string> hashes = new List<string>();
 /// <summary>手机网页“之前的”：这个时间以前的不给它看（点过清空）；超过多少天的自动不给（0 = 一直留）。</summary>
 public long histFrom; public int histDays = 7;
 public bool init; public string baseline, lastClip;
 /// <summary>快捷指令最后一次发来的图片：同一张图再来（比如切 App 自动运行）就不重复发。</summary>
 public string imageHash;
 /// <summary>上一次快捷指令带来的手机剪贴板（哈希）和时间：用来判断手机这次是不是“刚复制的”。</summary>
 public string lastReport; public long lastReportAt;
 /// <summary>这部手机的快捷指令上次运行的时间（0 = 还没设置过自动同步）。</summary>
 public long swapAt;
 /// <summary>快捷指令配错了（get = 方法没改成 POST，body = 请求体选了 JSON / 表单）和时间；设置向导据此直接指出改哪里。</summary>
 public string swapBad; public long swapBadAt;
}

public sealed class Config {
 public int version = 2;
 public string deviceId, deviceName, familyId, familyKey;
 public bool syncText = true, syncFiles = true, notify = true, paused = false, autostart = true, sendTo = true;
 public bool shellApplied, welcomed;
 public int autoLimitMB = 200;
 /// <summary>电脑“动态”里的记录留多少天；0 = 一直留。</summary>
 public int feedDays = 30;
 /// <summary>中转地址：null 用内置的，"" 表示关掉。relayAt 是改动时间，组内以最新的为准。</summary>
 public string relay, relayKey; public long relayAt;
 /// <summary>这台电脑开着临时中转（自己当中转 + Cloudflare 临时隧道）。</summary>
 public bool relayTemp;
 /// <summary>2.2.4 起同名手机只留一张卡片：老数据里已经扫出来的重复卡片在第一次启动时并一次。</summary>
 public bool phonesMerged; public int phonesMergeVer;
 public string receiveFolder;
 public List<Member> members = new List<Member>();
 public List<string> blocked = new List<string>();
 public List<Phone> phones = new List<Phone>();
 /// <summary>已经收进“动态”的手机离线内容（小云条目 id）。</summary>
 public List<string> cloudSeen = new List<string>();
}

/// <summary>设置文件：%LOCALAPPDATA%\LanBridge\config.dat，用当前 Windows 账户加密（DPAPI）。</summary>
sealed class Store {
 public readonly string Root; public Config C;
 static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LanBridge/2/config");
 string FilePath => Path.Combine(Root, "config.dat");
 public bool FreshInstall;

 public Store(string root, Options o) {
  Root = root; Directory.CreateDirectory(root);
  if (File.Exists(FilePath)) {
   try { C = J.Parse<Config>(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser)); }
   catch (Exception e) {
    // 换了 Windows 账户或文件被复制到别的电脑时解不开：留一份备份，重新开始。
    Log.Write("设置文件读不了，重新开始：" + e.Message);
    try { File.Copy(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
    C = null;
   }
  }
  if (C == null) { C = new Config(); FreshInstall = true; }
  C.members ??= new List<Member>(); C.blocked ??= new List<string>(); C.phones ??= new List<Phone>();
  foreach (var m in C.members) m.addrs ??= new List<string>();
  if (string.IsNullOrEmpty(C.deviceId)) C.deviceId = Crypto.Token(8);
  if (!string.IsNullOrEmpty(o.Name)) C.deviceName = o.Name;
  if (string.IsNullOrEmpty(C.deviceName)) C.deviceName = Environment.MachineName;
  if (string.IsNullOrEmpty(C.familyKey)) NewFamily();
  if (string.IsNullOrEmpty(C.receiveFolder)) C.receiveFolder = o.Data != null ? Path.Combine(root, "收到的文件") : Shell.DefaultReceiveFolder();
  if (C.autoLimitMB < 1) C.autoLimitMB = 200;
  if (string.IsNullOrEmpty(C.relayKey)) C.relayKey = Crypto.Token(16);
  if (o.Relay != null && C.relay == null) { C.relay = o.Relay; C.relayAt = 1; }
  Save();
 }

 public void NewFamily() { C.familyId = Crypto.Token(8); C.familyKey = Convert.ToBase64String(Crypto.Random(32)); C.members = new List<Member>(); }

 readonly object saveGate = new object();
 public void Save() {
  lock (saveGate) {
   var bytes = ProtectedData.Protect(J.Bytes(C), Entropy, DataProtectionScope.CurrentUser);
   string tmp = FilePath + ".tmp"; File.WriteAllBytes(tmp, bytes);
   if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
  }
 }
}
