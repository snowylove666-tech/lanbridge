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
 public bool init; public string baseline, lastClip;
}

public sealed class Config {
 public int version = 2;
 public string deviceId, deviceName, familyId, familyKey;
 public bool syncText = true, syncFiles = true, notify = true, paused = false, autostart = true, sendTo = true;
 public bool shellApplied, welcomed;
 public int autoLimitMB = 200;
 public string receiveFolder;
 public List<Member> members = new List<Member>();
 public List<string> blocked = new List<string>();
 public List<Phone> phones = new List<Phone>();
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
