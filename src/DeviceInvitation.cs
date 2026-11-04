using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LanBridge {
 public sealed class DeviceInvitation {
  public int version; public string device,secret; public string[] addresses;
  public static bool IsBundle(string text) { return (text??"").Contains("LB2:"); }
  public static string Create(BridgeState state) {
   lock(state.Gate) {
    if(DateTime.UtcNow>=state.PairUntil || String.IsNullOrEmpty(state.PairSecret)) state.RefreshPairing();
    var addresses=BridgeState.Addresses().Select(x=>"http://"+x+":"+state.Port).Take(12).ToList();
    addresses.Add("http://"+state.LocalHost+":"+state.Port); if(state.RemoteOrigin!="") addresses.Add(state.RemoteOrigin);
    var invitation=new DeviceInvitation {version=2,device=state.Prefs.installId,secret=state.PairSecret,addresses=addresses.Distinct().ToArray()};
    return "轻桥电脑连接邀请\r\nLB2:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(BridgeState.Json().Serialize(invitation)))+"\r\n在另一台轻桥的“连接电脑”粘贴并连接，程序优先尝试直连，无需判断网络类型。邀请五分钟内一次有效。";
   }
  }
  public static DeviceInvitation Parse(string text) {
   text=(text??"").Trim(); if(text.Length>16000) throw new ApiError(400,"邀请内容过长，请只粘贴一份邀请");
   var matches=Regex.Matches(text,@"LB2:([A-Za-z0-9+/=]+)"); if(matches.Count!=1) throw new ApiError(400,"请粘贴一份完整的电脑邀请");
   DeviceInvitation invite; try { invite=BridgeState.Json().Deserialize<DeviceInvitation>(new UTF8Encoding(false,true).GetString(Convert.FromBase64String(matches[0].Groups[1].Value))); } catch { throw new ApiError(400,"邀请不完整，请重新复制"); }
   if(invite==null || invite.version!=2 || !Regex.IsMatch(invite.device??"",@"\A[a-f0-9]{32}\z") || !Regex.IsMatch(invite.secret??"",@"\A[a-f0-9]{32}\z") || invite.addresses==null || invite.addresses.Length==0 || invite.addresses.Length>16) throw new ApiError(400,"邀请信息无效");
   foreach(string address in invite.addresses) { Uri uri; if(!Uri.TryCreate(address,UriKind.Absolute,out uri) || uri.Fragment!="") throw new ApiError(400,"邀请中的连接地址无效"); }
   invite.addresses=invite.addresses.Select(PeerClient.Normalize).Distinct().OrderBy(x=>x.StartsWith("https://")).ToArray(); return invite;
  }
 }
}
