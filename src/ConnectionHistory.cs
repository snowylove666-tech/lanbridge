using System;
using System.Linq;

namespace LanBridge {
 public sealed class PeerHistoryEntry { public string id,url,token,serverId,name; public bool clipboard; public DateTime lastUsed; }
 public sealed class RecentPeer { public string id,url,name; public bool remote,legacy; public DateTime lastUsed; }
 public sealed partial class BridgeState {
  public void RememberPeer(PeerSettings peer) {
   lock(Gate) {
    if(Prefs.peerHistory==null) Prefs.peerHistory=new System.Collections.Generic.List<PeerHistoryEntry>();
    var prior=Prefs.peerHistory.FirstOrDefault(x=>x.serverId==peer.serverId && (x.url==peer.url || (x.url.StartsWith("http://") && peer.url.StartsWith("http://"))));
    if(prior==null) { prior=new PeerHistoryEntry {id=RandomToken(8)}; Prefs.peerHistory.Add(prior); }
    prior.url=PeerClient.Normalize(peer.url); prior.token=peer.token; prior.serverId=peer.serverId; prior.name=peer.name; prior.clipboard=peer.clipboard; prior.lastUsed=DateTime.UtcNow;
    Prefs.peerHistory=Prefs.peerHistory.OrderByDescending(x=>x.lastUsed).Take(12).ToList(); SaveSettings();
   }
  }
  public PeerSettings FindRememberedPeer(string serverId,string url) {
   lock(Gate) {
    var entries=Prefs.peerHistory??new System.Collections.Generic.List<PeerHistoryEntry>();
    var found=entries.Where(x=>x.serverId==serverId && (x.url==url || (url.StartsWith("http://") && x.url.StartsWith("http://")))).OrderByDescending(x=>x.url==url).ThenByDescending(x=>x.lastUsed).FirstOrDefault();
    if(found!=null) return new PeerSettings {url=url,token=found.token,serverId=found.serverId,name=found.name,clipboard=found.clipboard};
    var old=Prefs.peer; return old!=null && old.serverId==serverId?old:null;
   }
  }
  public RecentPeer[] GetRecentPeers() {
   lock(Gate) {
    var all=(Prefs.peerHistory??new System.Collections.Generic.List<PeerHistoryEntry>()).Select(x=>new RecentPeer {id=x.id,url=x.url,name=x.name,remote=x.url.StartsWith("https://"),lastUsed=x.lastUsed}).ToList();
    if(Prefs.peer!=null && !String.IsNullOrEmpty(Prefs.peer.token) && !all.Any(x=>x.url==Prefs.peer.url)) all.Add(new RecentPeer {id="@legacy",url=Prefs.peer.url,name=Prefs.peer.name,remote=Prefs.peer.url.StartsWith("https://"),legacy=true});
    return all.OrderByDescending(x=>x.lastUsed).Take(12).ToArray();
   }
  }
  public void ReconnectPeer(string id) {
   PeerSettings selected;
   lock(Gate) {
    if(Paused) throw new ApiError(503,"互传已暂停，请先恢复");
    if(id=="@legacy") selected=Prefs.peer;
    else { var entry=(Prefs.peerHistory??new System.Collections.Generic.List<PeerHistoryEntry>()).FirstOrDefault(x=>x.id==id); selected=entry==null?null:new PeerSettings {url=entry.url,token=entry.token,serverId=entry.serverId,name=entry.name,clipboard=entry.clipboard}; }
   }
   if(selected==null) throw new ApiError(404,"历史连接已移除，请重新添加设备");
   Peer.Connect(selected.url,selected.clipboard);
  }
  public void ForgetRecentPeer(string id) { lock(Gate) { if(id=="@legacy") Prefs.peer=null; else if(Prefs.peerHistory!=null) {var prior=Prefs.peerHistory.FirstOrDefault(x=>x.id==id); if(prior!=null && Prefs.peer!=null && prior.url==Prefs.peer.url) Prefs.peer=null; Prefs.peerHistory.RemoveAll(x=>x.id==id);} SaveSettings(); } Changed(); }
 }
}
