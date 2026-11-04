using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LanBridge {
 public sealed class PeerSettings { public string url,token,serverId,name; public bool enabled=true,clipboard=true; }
 public sealed class DesktopClip { public string id,text,source; public long stamp; [System.Web.Script.Serialization.ScriptIgnore] public Func<bool> allow; [System.Web.Script.Serialization.ScriptIgnore] public string origin; }
 public sealed partial class BridgeState {
  public long DesktopStamp;
  public Func<DesktopClip,int> WriteDesktopClipboard;
  public PeerClient Peer;
  public static long NowMs() { return (DateTime.UtcNow.Ticks-621355968000000000L)/10000; }
  public DesktopClip DesktopSnapshot() { lock(Gate) { ExpireClipboard(); return new DesktopClip { id=PcId,text=PcText,source=ClipSource,stamp=DesktopStamp }; } }
  public bool DesktopNewer(DesktopClip clip) { return clip.id!=PcId && (clip.stamp>DesktopStamp || (clip.stamp==DesktopStamp && String.CompareOrdinal(clip.id,PcId)>0)); }
  // Called on the clipboard's STA, with Gate held, immediately after writing.
  public void CommitDesktop(DesktopClip clip) {
   PcText=ClipText=clip.text; PcId=clip.id; PcOrigin=clip.origin??""; DesktopStamp=clip.stamp;
   ClipSource=CleanName(clip.source,"另一台电脑",40); ClipTime=PcTime=DateTime.UtcNow;
   Revision++; Activity=ClipSource+" 的文字已复制到本机"; Changed();
  }
  public int ApplyDesktop(DesktopClip clip) {
   if(clip==null || String.IsNullOrEmpty(clip.text) || !Regex.IsMatch(clip.id??"",@"\A[a-f0-9]{24}\z")) throw new ApiError(400,"电脑剪贴板事件无效");
   if(Encoding.UTF8.GetByteCount(clip.text)>1024*1024) throw new ApiError(413,"文字超过 1 MB");
   lock(Gate) { if(Paused) throw new ApiError(503,"互传已暂停"); if(!DesktopNewer(clip)) return 0; }
   return WriteDesktopClipboard==null ? -1 : WriteDesktopClipboard(clip);
  }
  public object DesktopExchange(Device device,string seen,bool initialize,DesktopClip incoming,bool observeChanges=false) {
   if(device.kind!="computer") throw new ApiError(403,"此接口需要电脑配对");
   lock(Gate) { CheckDevice(device); if(!Prefs.autoClipboard) { var current=DesktopSnapshot(); return new {serverNow=NowMs(),currentId=current.id,accepted=false,clip=(DesktopClip)null,autoClipboard=false}; } }
   int accepted=0;
   if(incoming!=null && !initialize) {
    long now=NowMs();
    if(incoming.stamp>now+5000) incoming.stamp=now+5000;
    if(incoming.stamp>=now-120000) { incoming.source=device.name; incoming.allow=delegate { return !Paused && Prefs.autoClipboard && Prefs.devices.Any(x=>x.id==device.id && x.hash==device.hash); }; accepted=ApplyDesktop(incoming); if(accepted<0) throw new ApiError(409,"剪贴板暂时被占用，正在重试"); }
   }
   lock(Gate) {
    CheckDevice(device); var clip=DesktopSnapshot();
    return new { serverNow=NowMs(),currentId=clip.id,accepted=accepted>0 || (incoming!=null && clip.id==incoming.id),clip=Prefs.autoClipboard && (!initialize || observeChanges) && clip.id!=seen && !String.IsNullOrEmpty(clip.text) && NowMs()-clip.stamp<=120000 ? clip : null,autoClipboard=Prefs.autoClipboard };
   }
  }
  public bool VisibleFile(Transfer file,Device device) { return String.IsNullOrEmpty(file.recipient) || file.recipient==device.id; }
  public Transfer AddFileForDevice(string path,string deviceId) {
   var info=new FileInfo(path);
   using(var s=info.OpenRead()) return ReceiveFile(s,info.Length,info.Name,"Windows",true,delegate { lock(Gate) { if(Paused) throw new ApiError(503,"互传已暂停"); if(!Prefs.devices.Any(x=>x.id==deviceId)) throw new ApiError(401,"设备已撤销"); } },deviceId);
  }
  public bool HasReceived(string key) { lock(Gate) return Files.Any(x=>x.remoteKey==key && File.Exists(x.path)); }
  public void AcknowledgeFile(string id,Device device) {
   lock(Gate) {
    var file=Files.Find(x=>x.id==id && x.recipient==device.id);
    if(file==null) throw new ApiError(404,"文件不存在或没有权限");
    file.delivered=true; SaveFiles(); Revision++;
   } Changed();
  }
 }
}
