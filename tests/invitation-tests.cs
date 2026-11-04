using System;
using LanBridge;

static class InvitationTests {
 static int checks;
 const string Key="0123456789abcdef0123456789abcdef";
 const string Url="https://example.invalid/#pair="+Key;
 static void Check(bool value,string name) { if(!value) throw new Exception(name); checks++; Console.WriteLine("PASS "+name); }
 static void Reject(string value,string name) { try { ConnectionInvitation.Parse(value); } catch(FormatException) { Check(true,name); return; } throw new Exception(name); }
 static void Main() {
  Check(ConnectionInvitation.Parse(Url)==Url,"Plain invitation is accepted");
  Check(ConnectionInvitation.Parse("  "+Url+"\r\n")==Url,"Leading and trailing whitespace are normalized");
  Check(ConnectionInvitation.Parse("HTTPS://EXAMPLE.INVALID/#pair="+Key.ToUpperInvariant())==Url,"Scheme, host, and hex key are normalized");
  string message=ConnectionInvitation.Build("https://example.invalid",Key);
  Check(message.StartsWith("轻桥电脑连接邀请") && message.Contains("无需扫码"),"Generated message explains desktop join without QR");
  Check(ConnectionInvitation.Parse(message)==Url,"Generated wrapped invitation round trips");
  Check(ConnectionInvitation.Parse("<"+Url+">")==Url,"Angle-bracket wrapped link is accepted");
  Check(ConnectionInvitation.Parse("[点击加入]("+Url+")")==Url,"A single labeled Markdown link is accepted");
  Check(ConnectionInvitation.Parse("连接邀请："+Url+"，请粘贴连接")==Url,"Chinese prose punctuation does not become part of the key");
  Reject(Url+"\nhttps://other.invalid/#pair="+Key,"Different invitation hosts are rejected");
  Check(ConnectionInvitation.Parse(Url+"\n"+Url)==Url,"Repeated copies of one invitation are accepted");
  Check(ConnectionInvitation.Parse("["+Url+"]("+Url+")")==Url,"URL-labeled Markdown invitation is accepted");
  Check(ConnectionInvitation.Parse("[HTTPS://EXAMPLE.INVALID/#pair="+Key.ToUpperInvariant()+"]("+Url+")")==Url,"Equivalent Markdown label and target normalize host and key case");
  Reject(Url+"\nhttps://example.invalid/#pair="+new string('a',32),"Different keys at the same host are rejected");
  Reject(Url+"\nhttp://example.invalid/#pair="+Key,"HTTP mixed with a valid invitation is rejected");
  Reject(Url+"\nhttps://example.invalid/path#pair="+Key,"Invalid URL mixed with a valid invitation is rejected");
  Reject(Url+"\nhttps://","Incomplete second URL cannot be silently ignored");
  Reject(Url+"https://example.invalid/#pair="+Key,"Embedded protocol text cannot hide a second link");
  Reject("http://example.invalid/#pair="+Key,"Public HTTP invitations are rejected");
  Reject("https://example.invalid/","Missing pairing key is rejected");
  Reject("https://example.invalid/#pair=123456","Short PIN cannot masquerade as a remote invitation");
  Reject("https://example.invalid/#pair="+Key+"0","Overlong key is rejected");
  Reject("https://example.invalid/#pair="+new string('g',32),"Non-hex key is rejected");
  Reject("https://user:password@example.invalid/#pair="+Key,"Embedded credentials are rejected");
  Reject("https://example.invalid/path#pair="+Key,"Non-root path is rejected");
  Reject("https://example.invalid/?extra=yes#pair="+Key,"Query parameters are rejected");
  Reject(Url+"\0","Embedded control characters are rejected");
  Reject(new string('x',4097)+Url,"Oversized clipboard payload is rejected");
  Reject("轻桥连接邀请，没有地址","Prose without a link is rejected");
  Reject(null,"Null input is rejected");
  bool badOrigin=false; try { ConnectionInvitation.Build("https://user:password@example.invalid",Key); } catch(FormatException) { badOrigin=true; }
  Check(badOrigin,"Build cannot produce an invitation with embedded credentials");
  Console.WriteLine(checks+" invitation checks passed. No network or system clipboard was used.");
 }
}
