using System;
using System.Text.RegularExpressions;

namespace LanBridge {
 public static class ConnectionInvitation {
  const int MaxText=4096;
  static FormatException Invalid() { return new FormatException("邀请不完整，请复制对方轻桥生成的完整 HTTPS 连接邀请；应包含 #pair= 后的密钥。"); }
  public static string Parse(string text) {
   text=(text??"").Trim();
   if(text.Length==0 || text.Length>MaxText) throw Invalid();
   foreach(char ch in text) if(Char.IsControl(ch) && ch!='\r' && ch!='\n' && ch!='\t') throw Invalid();
   // Sharing apps may repeat the same URL as a Markdown label and target.
   // Accept equivalent repetitions, but never silently choose a different link.
   var starts=Regex.Matches(text,@"https?://",RegexOptions.IgnoreCase);
   if(starts.Count==0) throw new FormatException("未找到完整 HTTPS 邀请，请重新复制。");
   var matches=Regex.Matches(text,@"https?://[^\s<>""'，。；！？、（）\[\]{}()]+",RegexOptions.IgnoreCase);
   if(matches.Count!=starts.Count) throw Invalid();
   string invitation=null;
   foreach(Match match in matches) {
    string candidate=ParseLink(match.Value);
    if(invitation!=null && invitation!=candidate) throw new FormatException("邀请中包含不同的链接，请只粘贴一台电脑的一份邀请。");
    invitation=candidate;
   }
   return invitation;
  }
  static string ParseLink(string value) {
   if(value.Length>2048) throw Invalid();
   Uri uri;
   if(!Uri.TryCreate(value,UriKind.Absolute,out uri) || uri.Scheme!="https" || uri.Host=="" || uri.UserInfo!="" || uri.AbsolutePath!="/" || uri.Query!="") throw Invalid();
   if(!Regex.IsMatch(uri.Fragment,@"\A#pair=[a-fA-F0-9]{32}\z")) throw Invalid();
   return uri.GetLeftPart(UriPartial.Authority)+"/#pair="+uri.Fragment.Substring(6).ToLowerInvariant();
  }
  public static string Build(string origin,string key) {
   // Validate before producing shareable text; do not include user or machine data.
   Uri host;
   if(!Uri.TryCreate((origin??"").Trim(),UriKind.Absolute,out host) || host.Scheme!="https" || host.UserInfo!="" || host.AbsolutePath!="/" || host.Query!="" || host.Fragment!="") throw Invalid();
   string invitation=Parse(host.GetLeftPart(UriPartial.Authority)+"/#pair="+(key??""));
   return "轻桥电脑连接邀请"+Environment.NewLine+invitation+Environment.NewLine+"在另一台电脑的轻桥中选择“不同网络 → 我有邀请”，粘贴并连接，无需扫码。邀请五分钟内有效，可连接一台设备。";
  }
 }
}
