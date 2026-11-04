using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace LanBridge {
 public sealed class ApiError : Exception {
  public int Status; public ApiError(int status, string message) : base(message) { Status = status; }
 }
 public sealed class Device {
  public string id, name, hash; public DateTime added, seen;
  public string baselineHash, lastPcId;
  public bool syncInitialized;
  public bool temporary;
  public string kind;
  [ScriptIgnore] public readonly object SyncGate = new object();
  [ScriptIgnore] public SyncOffer offer;
  [ScriptIgnore] public string lastAckReceipt;
 }
 public sealed class SyncOffer { public string receipt, text, pcId; public DateTime until; }
 public sealed class JoinRequest { public string key, check, name, address, token, deviceId, kind; public DateTime until; public bool rejected; }
 public sealed class Transfer {
  public string id, name, path, direction, device, recipient, remoteKey; public long size; public DateTime time; public bool delivered;
  public AttachmentInfo attachment; public string attachmentOwner; public bool attachmentHandled;
  public object Public() { return new { id, name, size, direction, device, targeted=!String.IsNullOrEmpty(recipient), delivered, attachment, time = time.ToUniversalTime().ToString("o") }; }
 }
 public sealed class Preferences { public bool autoClipboard = false; public bool attachmentClipboard=false; public List<Device> devices = new List<Device>(); public PeerSettings peer; public List<PeerHistoryEntry> peerHistory = new List<PeerHistoryEntry>(); public string installId; }
 public sealed partial class BridgeState {
  public const long MaxFile = 1024L * 1024 * 1024;
  public const long MaxQuota = 5L * 1024 * 1024 * 1024;
  public readonly object Gate = new object();
  public readonly string Root, Received, Outgoing;
  public readonly int Port;
  public readonly string LocalHost;
  public string DiscoveryStatus="本地地址启动中", RemoteOrigin="", RemoteStatus="跨网连接未开启";
  internal string RemoteCandidateOrigin=""; internal DateTime RemoteCandidateUntil;
  public DateTime RemoteUntil;
  public volatile bool RemoteRunning;
  public volatile bool RemoteVerified;
  public string RemoteDiagnostic="";
  public Action ToggleRemote=delegate {};
  public readonly List<JoinRequest> Joins=new List<JoinRequest>();
  public Preferences Prefs = new Preferences();
  public List<Transfer> Files = new List<Transfer>();
  public string PairCode, PairSecret;
  public DateTime PairUntil;
  public volatile bool Paused;
  public string ClipText = "", ClipSource = "";
  public DateTime ClipTime;
  public string PcText = "", PcId = "", PcOrigin = "";
  public DateTime PcTime;
  public long Revision;
  public string Activity = "等待设备连接";
  public Action Changed = delegate {};
  private int failedPairs;
  private DateTime pairBlocked;
  sealed class PairReply { public string request,codeHash,token,id,name,kind; public bool remote; public DateTime until; }
  readonly List<PairReply> pairReplies=new List<PairReply>();
  private long reserved;
  private int pendingFiles;
  public BridgeState(string root, int port) {
   Root = root; Port = port;
   using(var sha=SHA256.Create()) LocalHost="qiao-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Environment.MachineName+"|"+port))).Replace("-","").Substring(0,6).ToLowerInvariant()+".local";
   Received = Path.Combine(root, "Received"); Outgoing = Path.Combine(root, "Data", "Outbox");
   Directory.CreateDirectory(Path.Combine(root, "Data")); Directory.CreateDirectory(Received); Directory.CreateDirectory(Outgoing);
   Load(); if(String.IsNullOrEmpty(Prefs.installId)) { Prefs.installId=RandomToken(16); SaveSettings(); } RefreshPairing();
  }
  public static string RandomToken(int length) { byte[] bytes = new byte[length]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
  public static string Hash(string token) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? ""))); }
  public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 2097152, RecursionLimit = 32 }; }
  private void Load() {
   string cfg = Path.Combine(Root, "Data", "settings.bin");
   if (File.Exists(cfg)) { try { Prefs = Json().Deserialize<Preferences>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(cfg), null, DataProtectionScope.CurrentUser))); } catch { throw new InvalidDataException("无法读取配对设置。请使用原 Windows 账户，或备份后移走 Data/settings.bin 重新配对。"); } }
   if (Prefs == null || Prefs.devices == null) throw new InvalidDataException("配对设置损坏");
   Prefs.devices.RemoveAll(x=>x.temporary);
   string index = Path.Combine(Root, "Data", "files.json");
   if (File.Exists(index)) {
    try { Files = Json().Deserialize<List<Transfer>>(File.ReadAllText(index, Encoding.UTF8)) ?? new List<Transfer>(); }
    catch { throw new InvalidDataException("文件索引损坏。Received 文件仍保留；请备份 Data/files.json 后移走它重新启动。"); }
    Files = Files.Where(f => f != null && f.path != null && Inside(f.path, f.direction == "toPhone" ? Outgoing : Received) && File.Exists(f.path)).ToList();
   }
   foreach (string p in Directory.GetFiles(Outgoing, "*.part").Concat(Directory.GetFiles(Received, "*.part"))) {
    string stem=Path.GetFileNameWithoutExtension(p);
    if(stem.Length==24 && stem.All(c=>"0123456789abcdef".Contains(c))) try { File.Delete(p); } catch {}
   }
  }
  public static bool Inside(string path, string folder) { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
  static void AtomicWrite(string path, byte[] bytes) {
   string temp = path + ".tmp"; File.WriteAllBytes(temp, bytes);
   if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
  }
  public void SaveSettings() { lock (Gate) { AtomicWrite(Path.Combine(Root,"Data","settings.bin"), ProtectedData.Protect(Encoding.UTF8.GetBytes(Json().Serialize(Prefs)), null, DataProtectionScope.CurrentUser)); } }
  void SaveFiles() { AtomicWrite(Path.Combine(Root,"Data","files.json"), Encoding.UTF8.GetBytes(Json().Serialize(Files))); }
  public void RefreshPairing() {
   lock (Gate) { PairSecret = RandomToken(16); byte[] b = new byte[4]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); PairCode = (BitConverter.ToUInt32(b,0) % 1000000).ToString("D6"); PairUntil = DateTime.UtcNow.AddMinutes(5); }
   Changed();
  }
  public object Pair(string code, string name, bool remote=false, string kind="phone",string requestId="") {
   string token = RandomToken(32); Device d;
   lock (Gate) {
    if (Paused) throw new ApiError(503,"互传已暂停，请在电脑恢复");
    pairReplies.RemoveAll(x=>x.until<DateTime.UtcNow || !Prefs.devices.Any(device=>device.id==x.id));
    if(requestId!="") {
     if(requestId.Length!=32 || requestId.Any(x=>!(x>='0' && x<='9') && !(x>='a' && x<='f'))) throw new ApiError(400,"配对请求标识无效");
     var previous=pairReplies.Find(x=>x.request==requestId && x.codeHash==Hash(code) && x.remote==remote && x.kind==(kind=="computer"?"computer":"phone"));
     if(previous!=null) return new {token=previous.token,id=previous.id,name=previous.name};
    }
    if (DateTime.UtcNow < pairBlocked) throw new ApiError(429,"尝试过多，请 30 秒后重试");
    if (DateTime.UtcNow > PairUntil || !(code == PairSecret || (!remote && code == PairCode))) {
     if (++failedPairs >= 5) { pairBlocked = DateTime.UtcNow.AddSeconds(30); failedPairs = 0; }
     throw new ApiError(403,"配对码错误或已过期，请在电脑刷新");
    }
    if (Prefs.devices.Count >= 10) throw new ApiError(409,"已配对 10 台设备，请先在电脑移除旧设备");
    d = new Device { id = RandomToken(8), name = CleanName(name, "我的 iPhone", 40), hash = Hash(token), added = DateTime.UtcNow, seen = DateTime.UtcNow,temporary=remote,kind=kind=="computer"?"computer":"phone" };
    Prefs.devices.Add(d); try { SaveSettings(); } catch { Prefs.devices.Remove(d); throw; }
    if(requestId!="") { if(pairReplies.Count>=32) pairReplies.RemoveAt(0); pairReplies.Add(new PairReply {request=requestId,codeHash=Hash(code),token=token,id=d.id,name=d.name,kind=d.kind,remote=remote,until=DateTime.UtcNow.AddMinutes(3)}); }
    PairUntil = DateTime.MinValue; PairSecret = ""; PairCode = "------"; failedPairs = 0;
    Activity = d.name + " 已配对"; Revision++;
   }
   Changed(); return new { token, id = d.id, name = d.name };
  }
  public object RequestJoin(string name,string address,string kind="phone") {
   JoinRequest j;
   lock(Gate) {
    if(Paused) throw new ApiError(503,"互传已暂停");
    Joins.RemoveAll(x=>x.until<DateTime.UtcNow);
    if(Joins.Count>=5) throw new ApiError(429,"连接请求较多，请两分钟后重试");
    j=new JoinRequest { key=RandomToken(32),check=(BitConverter.ToUInt32(Convert.FromBase64String(Hash(RandomToken(8))),0)%10000).ToString("D4"),name=CleanName(name,"我的 iPhone",40),address=address,until=DateTime.UtcNow.AddMinutes(2),kind=kind=="computer"?"computer":"phone" };
    Joins.Add(j); Activity=j.name+" 请求连接，请核对识别号 "+j.check;
   }
   Changed(); return new { key=j.key,check=j.check };
  }
  public object PollJoin(string key) {
   lock(Gate) {
    if(Paused) throw new ApiError(503,"互传已暂停");
    var j=Joins.Find(x=>x.key==key && x.until>DateTime.UtcNow);
    if(j==null) throw new ApiError(403,"连接请求已过期，请重新连接");
    if(j.rejected) throw new ApiError(403,"电脑已拒绝连接请求");
    if(j.token==null) return new { pending=true };
    if(!Prefs.devices.Any(x=>x.id==j.deviceId)) throw new ApiError(401,"设备已撤销");
    return new { pending=false,token=j.token,name=j.name };
   }
  }
  public void ApproveJoin(string key,bool approve) {
   lock(Gate) {
    var j=Joins.Find(x=>x.key==key && x.until>DateTime.UtcNow && x.token==null && !x.rejected);
    if(j==null) return;
    if(!approve) { j.rejected=true; Activity="已拒绝连接请求"; }
    else {
     if(Paused) throw new ApiError(503,"互传已暂停");
     if(Prefs.devices.Count>=10) throw new ApiError(409,"最多配对 10 台设备，请先移除旧设备");
     string token=RandomToken(32); var d=new Device { id=RandomToken(8),name=j.name,hash=Hash(token),added=DateTime.UtcNow,seen=DateTime.UtcNow,kind=j.kind };
     Prefs.devices.Add(d); try { SaveSettings(); } catch { Prefs.devices.Remove(d); throw; }
     j.token=token; j.deviceId=d.id; Activity=d.name+" 已连接，以后自动重连"; Revision++;
    }
   } Changed();
  }
  public Device Authenticate(string token) {
   lock (Gate) {
    if (Paused) throw new ApiError(503,"互传已暂停，请在电脑恢复");
    string hash = Hash(token); var d = Prefs.devices.Find(x => x.hash == hash);
    if (d == null) throw new ApiError(401,"请先配对，或重新连接这台电脑");
    d.seen = DateTime.UtcNow; return d;
   }
  }
  public void CloseRemote() {
   lock(Gate) {
    RemoteOrigin=""; RemoteUntil=DateTime.MinValue; RemoteCandidateOrigin=""; RemoteCandidateUntil=DateTime.MinValue; RemoteVerified=false;
    if(Prefs.devices.RemoveAll(x=>x.temporary)>0) try { SaveSettings(); } catch { /* Startup also drops any old temporary credentials. */ }
   }
  }
  public void PublishClipboard(string text, string source, bool fromPhone, string sourceId=null) {
   if (String.IsNullOrEmpty(text)) throw new ApiError(400,"剪贴板没有文字，请先复制文字");
   if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024) throw new ApiError(413,"文字超过 1 MB，请改用文件传输");
   if(fromPhone) {
    DesktopClip incoming;
    lock(Gate) {
     if(Paused) throw new ApiError(503,"互传已暂停");
     if(sourceId!=null && !Prefs.devices.Any(x=>x.id==sourceId)) throw new ApiError(401,"设备已撤销配对");
     incoming=new DesktopClip {id=RandomToken(12),text=text,source=source,stamp=Math.Max(NowMs(),DesktopStamp+1),origin=sourceId};
     incoming.allow=delegate { return !Paused && (sourceId==null || Prefs.devices.Any(x=>x.id==sourceId)); };
    }
    int applied=ApplyDesktop(incoming);
    if(applied<0) throw new ApiError(409,"电脑剪贴板暂时被占用，请重试");
    if(applied==0) { lock(Gate) { if(Paused) throw new ApiError(503,"互传已暂停"); if(sourceId!=null && !Prefs.devices.Any(x=>x.id==sourceId)) throw new ApiError(401,"设备已撤销配对"); } throw new ApiError(409,"电脑有更新的复制内容，请重新同步"); }
    return;
   }
   lock (Gate) {
    if(Paused) throw new ApiError(503,"互传已暂停");
    ClipText = text; ClipSource = source; ClipTime = DateTime.UtcNow; Revision++;
    DesktopStamp=Math.Max(NowMs(),DesktopStamp+1);
    PcText=text; PcId=RandomToken(12); PcTime=ClipTime; PcOrigin=fromPhone?(sourceId??""):"";
    Activity = "电脑文字已就绪，等待设备接收";
   }
   Changed();
  }
  public object ClipboardView() {
   lock (Gate) { ExpireClipboard(); bool valid = ClipText.Length > 0; return new { text = valid ? ClipText : "", source = valid ? ClipSource : "", time = valid ? ClipTime.ToString("o") : null, expiresIn = valid ? Math.Max(0, (int)(600 - (DateTime.UtcNow - ClipTime).TotalSeconds)) : 0 }; }
  }
  public void ExpireClipboard() { lock(Gate) {
   Joins.RemoveAll(x=>x.until<DateTime.UtcNow);
   if(ClipText.Length>0 && DateTime.UtcNow-ClipTime>=TimeSpan.FromMinutes(10)) { ClipText=""; ClipSource=""; Revision++; }
   if(PcText.Length>0 && DateTime.UtcNow-PcTime>=TimeSpan.FromMinutes(10)) { PcText=""; PcId=""; PcOrigin=""; }
   foreach(var d in Prefs.devices) if(d.offer!=null && d.offer.until<DateTime.UtcNow) d.offer=null;
  } }
  public string ClipboardText() { lock (Gate) { ExpireClipboard(); if (ClipText.Length == 0) throw new ApiError(404,"暂无可接收的文字，请在电脑复制并发送"); return ClipText; } }
  public void ClearClipboard() { lock (Gate) { ClipText = ""; ClipSource = ""; PcText=""; PcId=""; PcOrigin=""; foreach(var d in Prefs.devices) d.offer=null; Revision++; Activity = "已清除共享文字（系统剪贴板保留）"; } Changed(); }
  public void RememberPhone(Device device,string text) { lock(Gate) { device.baselineHash=Hash(text); device.syncInitialized=true; device.lastPcId=PcId; device.offer=null; SaveSettings(); } }
  public object Exchange(Device device,string text,string mode) {
   if(mode!="smart" && mode!="receive" && mode!="receive-new" && mode!="send") throw new ApiError(400,"同步模式无效");
   if(Encoding.UTF8.GetByteCount(text)>1024*1024) throw new ApiError(413,"文字超过 1 MB，请改用文件传输");
   lock(device.SyncGate) {
    lock(Gate) {
     CheckDevice(device); ExpireClipboard();
     if(mode=="smart" && !device.syncInitialized) {
      device.syncInitialized=true; device.baselineHash=Hash(text); device.lastPcId=PcId; SaveSettings();
      Activity=device.name+" 的智能同步已初始化"; Changed();
      return SyncResult("ready",false,"","","首次初始化完成。之后在任一端复制新文字，再运行同一个指令即可。");
     }
     bool pcNew=PcText.Length>0 && ((PcId!=device.lastPcId && PcOrigin!=device.id) || mode=="receive");
     if(mode=="smart" || mode=="receive-new") pcNew=pcNew && DateTime.UtcNow-PcTime<=TimeSpan.FromSeconds(120);
     bool phoneNew=device.syncInitialized && Hash(text)!=device.baselineHash;
     if(mode=="smart" && pcNew && text==PcText) { device.baselineHash=Hash(text); device.lastPcId=PcId; device.offer=null; SaveSettings(); return SyncResult("noop",false,"","","两端文字一致，无需重复同步。"); }
     if((mode=="receive" || mode=="receive-new" || mode=="smart") && pcNew && (mode!="smart" || !phoneNew)) {
      if(device.offer==null || device.offer.pcId!=PcId) device.offer=new SyncOffer { receipt=RandomToken(16),text=PcText,pcId=PcId,until=DateTime.UtcNow.AddMinutes(1) };
      return SyncResult("receive",true,device.offer.text,device.offer.receipt,"已取回电脑文字，复制成功后确认即可。");
     }
     if(mode=="receive" || mode=="receive-new" || (mode=="smart" && !phoneNew)) return SyncResult("noop",false,"","","没有新内容，已保留手机原剪贴板。");
    }
    if(String.IsNullOrEmpty(text)) return SyncResult("noop",false,"","","手机没有文字，未改动电脑剪贴板。");
    PublishClipboard(text,device.name,true,device.id);
    lock(Gate) { CheckDevice(device); device.baselineHash=Hash(text); device.syncInitialized=true; device.lastPcId=PcId; device.offer=null; SaveSettings(); }
    return SyncResult("sent",false,"","","已发送到电脑，可以直接 Ctrl+V。");
   }
  }
  void CheckDevice(Device device) { if(Paused) throw new ApiError(503,"互传已暂停"); if(!Prefs.devices.Contains(device)) throw new ApiError(401,"设备已撤销配对"); }
  public void Acknowledge(Device device,string receipt) {
   lock(device.SyncGate) lock(Gate) {
    CheckDevice(device); ExpireClipboard(); var offer=device.offer;
    if(receipt.Length>0 && device.lastAckReceipt==receipt) return;
    if(offer==null || offer.receipt!=receipt) throw new ApiError(409,"接收确认已过期，请重新接收");
    device.baselineHash=Hash(offer.text); device.syncInitialized=true; device.lastPcId=offer.pcId; device.lastAckReceipt=receipt; device.offer=null; SaveSettings();
    Activity=device.name+" 已确认接收电脑文字"; Revision++;
   } Changed();
  }
  static object SyncResult(string action,bool copy,string text,string receipt,string message) { return new { ok=true,action,copy,text,receipt,message }; }
  public static string CleanName(string s, string fallback, int max) { if (String.IsNullOrWhiteSpace(s)) return fallback; s = new string(s.Where(c => !Char.IsControl(c)).ToArray()).Trim(); return s.Length == 0 ? fallback : s.Substring(0, Math.Min(s.Length,max)); }
  public static string SafeFileName(string s) {
   s = (s ?? "file").Replace('\\','/'); s = s.Substring(s.LastIndexOf('/')+1);
   foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
   s = CleanName(s,"file",150).TrimEnd('.', ' '); if (s.Length == 0) s = "file";
   string stem = Path.GetFileNameWithoutExtension(s).ToUpperInvariant();
   if (new [] {"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem)) s = "_" + s;
   return s;
  }
  public Transfer ReceiveFile(Stream body, long declared, string filename, string device, bool toPhone, Action checkPermission,string recipient=null,string remoteKey=null,AttachmentInfo attachment=null,string attachmentOwner=null) {
   if (declared > MaxFile) throw new ApiError(413,"单个文件最大 1 GB");
   if (declared < 0) throw new ApiError(411,"文件需要 Content-Length，请使用手机网页上传");
   string id = RandomToken(12), name = SafeFileName(filename), folder = toPhone ? Outgoing : Received;
   string path = Path.Combine(folder, id + "_" + name), part = Path.Combine(folder, id + ".part");
   lock (Gate) {
    if (Files.Count + pendingFiles >= 500) throw new ApiError(507,"已达到 500 个文件，请先清理传输记录");
    long stored = Directory.GetFiles(Received).Concat(Directory.GetFiles(Outgoing)).Where(p=>!p.EndsWith(".part",StringComparison.OrdinalIgnoreCase)).Sum(p=>new FileInfo(p).Length);
    if (stored + reserved + declared > MaxQuota) throw new ApiError(507,"传输区已达到 5 GB，请先清理文件"); reserved += declared; pendingFiles++;
   }
   try {
    long total = 0; byte[] buffer = new byte[65536];
    using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length)) {
     while (total < declared) { checkPermission(); int n = body.Read(buffer,0,(int)Math.Min(buffer.Length,declared-total)); if (n <= 0) throw new ApiError(400,"传输中断，未保存不完整文件"); file.Write(buffer,0,n); total += n; }
    }
    checkPermission(); File.Move(part,path);
    var f = new Transfer { id=id, name=name, path=path, size=total, direction=toPhone ? "toPhone" : "toPC", device=device, time=DateTime.UtcNow,recipient=recipient,remoteKey=remoteKey,attachment=attachment,attachmentOwner=attachmentOwner };
    lock (Gate) { Files.Add(f); try { SaveFiles(); } catch { Files.Remove(f); File.Delete(path); throw; } Revision++; Activity = toPhone ? name + " 已放入发送区" : "已收到 " + name; }
    Changed(); return f;
   } finally { lock (Gate) { reserved -= declared; pendingFiles--; } if (File.Exists(part)) File.Delete(part); }
  }
  public Transfer AddFile(string path) { var info = new FileInfo(path); using (var s = info.OpenRead()) return ReceiveFile(s,info.Length,info.Name,"Windows",true,delegate { if (Paused) throw new ApiError(503,"互传已暂停"); }); }
  public void RemoveFile(string id) {
   lock (Gate) {
    var f = Files.Find(x=>x.id == id); if (f==null) throw new ApiError(404,"文件不存在");
    // Incoming originals belong to the user. Removing their record never deletes them.
    if (f.direction == "toPhone" && File.Exists(f.path)) File.Delete(f.path);
    Files.Remove(f); SaveFiles(); Revision++;
   } Changed();
  }
  public static bool PrivateAddress(IPAddress ip) { byte[] b=ip.GetAddressBytes(); return b.Length==4 && (b[0]==10 || b[0]==127 || (b[0]==192 && b[1]==168) || (b[0]==172 && b[1]>=16 && b[1]<=31) || (b[0]==169 && b[1]==254)); }
  public static List<string> Addresses() {
   var list = new List<string>();
   foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) if (ni.OperationalStatus==OperationalStatus.Up && ni.NetworkInterfaceType!=NetworkInterfaceType.Loopback) foreach (var a in ni.GetIPProperties().UnicastAddresses) if (a.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork && PrivateAddress(a.Address)) list.Add(a.Address.ToString());
   return list.Distinct().OrderBy(x=>x.StartsWith("169.254.") ? 1 : 0).ToList();
  }
 }
}
