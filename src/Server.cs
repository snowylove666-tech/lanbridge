using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace LanBridge {
 public sealed class Request {
  public string Method, Path, Host; public Uri Url; public long Length;
  public Dictionary<string,string> Headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  public Stream Stream;
  public string Header(string name) { string value; return Headers.TryGetValue(name,out value) ? value : ""; }
  public string Query(string key) {
   foreach (string part in Url.Query.TrimStart('?').Split('&')) { var p=part.Split(new[]{'='},2); if (Uri.UnescapeDataString(p[0])==key) return p.Length==2 ? Uri.UnescapeDataString(p[1].Replace("+"," ")) : ""; } return "";
  }
  public Dictionary<string,object> Json() {
   if (!Header("Content-Type").StartsWith("application/json",StringComparison.OrdinalIgnoreCase)) throw new ApiError(415,"请使用 JSON 请求");
   if (Length<0 || Length>1100000) throw new ApiError(413,"请求过大");
   byte[] b=new byte[(int)Length]; int n=0;
   while(n<b.Length) { int got=Stream.Read(b,n,b.Length-n); if(got==0) throw new ApiError(400,"请求内容不完整"); n+=got; }
   try { return BridgeState.Json().Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(b)) ?? new Dictionary<string,object>(); } catch { throw new ApiError(400,"JSON 格式无效"); }
  }
  public static string Value(Dictionary<string,object> d,string key) { object value; return d.TryGetValue(key,out value) && value is string ? (string)value : ""; }
 }
 public sealed class Ticket { public string FileId, DeviceHash; public DateTime Until; }
 public sealed class BridgeServer : IDisposable {
  readonly BridgeState state; readonly TcpListener listener;
  readonly SemaphoreSlim slots=new SemaphoreSlim(12,12);
  readonly Dictionary<string,Ticket> tickets=new Dictionary<string,Ticket>();
  volatile bool running;
  readonly bool remoteMode;
  public int ListeningPort { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
  readonly string testKey;
  public BridgeServer(BridgeState s,string testKey,bool remote=false) { state=s; this.testKey=testKey; remoteMode=remote; listener=new TcpListener(remote?IPAddress.Loopback:IPAddress.Any,remote?0:state.Port); }
  public void Start() { listener.Start(24); running=true; new Thread(Accept) { IsBackground=true,Name="LAN listener" }.Start(); }
  public void Dispose() { running=false; listener.Stop(); }
  void Accept() {
   while(running) {
    TcpClient c; try { c=listener.AcceptTcpClient(); } catch { if(!running) return; continue; }
    var ip=((IPEndPoint)c.Client.RemoteEndPoint).Address;
    if (!(remoteMode?IPAddress.IsLoopback(ip):BridgeState.PrivateAddress(ip)) || !slots.Wait(0)) { c.Close(); continue; }
    ThreadPool.QueueUserWorkItem(delegate { try { Handle(c,ip); } finally { slots.Release(); c.Close(); } });
   }
  }
  Request Read(Stream stream) {
   var bytes=new List<byte>(); int b, matched=0;
   while((b=stream.ReadByte())!=-1) {
    bytes.Add((byte)b); if(bytes.Count>16384) throw new ApiError(431,"请求头过长");
    matched = b == new byte[]{13,10,13,10}[matched] ? matched+1 : (b==13 ? 1 : 0);
    if(matched==4) break;
   }
   if(matched!=4) throw new ApiError(400,"请求头不完整");
   string[] lines=Encoding.ASCII.GetString(bytes.ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None);
   var first=lines[0].Split(' ');
   if(first.Length!=3 || (first[2]!="HTTP/1.1" && first[2]!="HTTP/1.0") || !first[1].StartsWith("/") || first[1].StartsWith("//")) throw new ApiError(400,"无效请求");
   var r=new Request { Method=first[0],Stream=stream,Length=0 };
   for(int i=1;i<lines.Length && lines[i].Length>0;i++) {
    int colon=lines[i].IndexOf(':'); if(colon<1 || Char.IsWhiteSpace(lines[i][0])) throw new ApiError(400,"无效请求头");
    string k=lines[i].Substring(0,colon), v=lines[i].Substring(colon+1).Trim();
    if(r.Headers.ContainsKey(k)) throw new ApiError(400,"重复的请求头"); r.Headers[k]=v;
   }
   if(r.Header("Transfer-Encoding")!="") throw new ApiError(400,"暂不支持分块上传，请使用网页上传文件");
   string length=r.Header("Content-Length");
   if(length!="" && (!Int64.TryParse(length,NumberStyles.None,CultureInfo.InvariantCulture,out r.Length) || r.Length<0)) throw new ApiError(400,"无效 Content-Length");
   if(r.Length>BridgeState.MaxFile) throw new ApiError(413,"单个文件最大 1 GB");
   r.Host=r.Header("Host");
   Uri host; string scheme=remoteMode?"https":"http";
   if(!Uri.TryCreate(scheme+"://"+r.Host,UriKind.Absolute,out host) || host.UserInfo!="" || host.AbsolutePath!="/" || host.Query!="" || host.Fragment!="") throw new ApiError(403,"无效主机");
   if(remoteMode) { string expected=RemoteActive()?state.RemoteOrigin:CandidateActive()?state.RemoteCandidateOrigin:""; if(expected=="" || host.GetLeftPart(UriPartial.Authority)!=expected) throw new ApiError(403,"跨网连接未开启或地址不匹配"); }
   else {
    if(host.Port!=state.Port) throw new ApiError(403,"无效主机");
    var allowed=BridgeState.Addresses(); allowed.Add("127.0.0.1"); allowed.Add("localhost"); allowed.Add(state.LocalHost);
    if(!allowed.Contains(host.Host,StringComparer.OrdinalIgnoreCase)) throw new ApiError(403,"主机不匹配");
   }
   if(!Uri.TryCreate(host,first[1],out r.Url) || r.Url.Authority!=host.Authority || r.Url.Scheme!=scheme) throw new ApiError(400,"无效地址");
   r.Path=r.Url.AbsolutePath;
   string origin=r.Header("Origin");
   if(origin!="" && origin!=scheme+"://"+r.Host) throw new ApiError(403,"不接受其他网页的请求");
   if(r.Header("Sec-Fetch-Site")=="cross-site") throw new ApiError(403,"不接受跨站请求");
   return r;
  }
  void Handle(TcpClient client,IPAddress remote) {
   client.NoDelay=true; client.ReceiveTimeout=15000; client.SendTimeout=60000;
   using(var stream=client.GetStream()) {
    try {
     var r=Read(stream);
     if(r.Header("Expect").Equals("100-continue",StringComparison.OrdinalIgnoreCase)) { var cont=Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); stream.Write(cont,0,cont.Length); }
     Route(r,remote);
    } catch(ApiError ex) { TryError(stream,ex.Status,ex.Message); }
    catch(IOException) { }
    catch(SocketException) { }
    catch(Exception) { TryError(stream,500,"操作失败，请检查磁盘空间或重试"); }
    // Finish the response before closing. Drain a small rejected request body so
    // Windows does not replace the HTTP error with a TCP reset on unread data.
    try { stream.Flush(); client.Client.Shutdown(SocketShutdown.Send); client.ReceiveTimeout=1000; int remaining=4*1024*1024; byte[] drain=new byte[16384]; int n; while(remaining>0 && (n=stream.Read(drain,0,Math.Min(drain.Length,remaining)))>0) remaining-=n; } catch {}
   }
  }
  void Route(Request r,IPAddress remote) {
   if(remoteMode && !RemoteActive() && !(CandidateActive() && r.Path=="/health" && r.Method=="GET")) throw new ApiError(403,"跨网连接已关闭或正在验证");
   if(r.Method=="GET" && (r.Path=="/" || r.Path=="/app.js" || r.Path=="/style.css" || r.Path=="/icon.svg")) {
    string file=r.Path=="/" ? "index.html" : r.Path.Substring(1);
    using(var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("Web."+file)) {
     if(resource==null) throw new ApiError(404,"资源不存在");
     string type=file.EndsWith(".html")?"text/html; charset=utf-8":file.EndsWith(".js")?"application/javascript; charset=utf-8":file.EndsWith(".css")?"text/css; charset=utf-8":"image/svg+xml";
     Head(r.Stream,200,type,resource.Length,null); resource.CopyTo(r.Stream); return;
    }
   }
   if(r.Method=="GET" && r.Path=="/health") { string nonce=r.Query("nonce"); Json(r,new { ok=true,app="LanBridge",version="1.5.0",instance=state.Prefs.installId,nonce=nonce.Length<=64?nonce:"" }); return; }
   if(r.Method=="GET" && r.Path=="/v1/connection") { lock(state.Gate) { state.ExpireClipboard(); Json(r,new { remote=remoteMode,localUrl="http://"+state.LocalHost+":"+state.Port, maxFile=remoteMode?90L*1024*1024:BridgeState.MaxFile,computer=Environment.MachineName,serverId=state.Prefs.installId,desktop=true,desktopObserveChanges=true,clipboardAttachments=true,clipboardEventId=state.PcId }); } return; }
   if(r.Method=="POST" && r.Path=="/v1/pair") { var d=r.Json(); Json(r,state.Pair(Request.Value(d,"code"),Request.Value(d,"name"),remoteMode,Request.Value(d,"kind"),Request.Value(d,"requestId"))); return; }
   if(!remoteMode && r.Method=="POST" && r.Path=="/v1/join") { var d=r.Json(); Json(r,state.RequestJoin(Request.Value(d,"name"),remote.ToString(),Request.Value(d,"kind"))); return; }
   if(!remoteMode && r.Method=="POST" && r.Path=="/v1/join/poll") { var d=r.Json(); Json(r,state.PollJoin(Request.Value(d,"key"))); return; }
   if(remoteMode && r.Path.StartsWith("/v1/join")) throw new ApiError(403,"跨网连接请扫描电脑二维码");
   // Testing controls only exist in an explicitly started, isolated test instance.
   if(testKey!=null && IPAddress.IsLoopback(remote) && r.Header("Authorization")=="Bearer "+testKey && r.Path=="/test/control" && r.Method=="POST") {
    var j=r.Json(); string action=Request.Value(j,"action");
    if(action=="publish") state.PublishClipboard(Request.Value(j,"text"),"Windows",false);
    else if(action=="pause") { state.Paused=true; state.Changed(); }
    else if(action=="resume") { state.Paused=false; state.Changed(); }
    else if(action=="refresh") state.RefreshPairing();
    else if(action=="approve-join") state.ApproveJoin(Request.Value(j,"key"),true);
    else if(action=="reject-join") state.ApproveJoin(Request.Value(j,"key"),false);
    else if(action=="start-remote") state.ToggleRemote();
    else if(action=="stop-remote") state.CloseRemote();
    else if(action=="status") { }
    else if(action=="make-invite") {Json(r,new {invitation=DeviceInvitation.Create(state)});return;}
    else if(action=="set-attachments") {object flag;lock(state.Gate){state.Prefs.attachmentClipboard=j.TryGetValue("enabled",out flag) && flag is bool && (bool)flag;state.SaveSettings();}}
    else if(action=="history-reconnect") state.ReconnectPeer(Request.Value(j,"id"));
    else if(action=="history-forget") state.ForgetRecentPeer(Request.Value(j,"id"));
    else if(action=="peer-attachment-send" || action=="target-attachment") {
     object raw; string[] paths=j.TryGetValue("paths",out raw)?BridgeState.Json().ConvertToType<string[]>(raw):new[]{Request.Value(j,"path")};
     foreach(string path in paths)if(!BridgeState.Inside(path,state.Root))throw new ApiError(403,"测试附件必须位于隔离目录");
     if(action=="peer-attachment-send")state.Peer.SendClipboardFiles(paths,Request.Value(j,"kind"));else state.AddClipboardFilesForDevice(paths,Request.Value(j,"device"),Request.Value(j,"kind"));
    }
    else if(action=="discover-computers") { Json(r,new { computers=ComputerDiscovery.Scan(state.Prefs.installId) }); return; }
    else if(action=="set-auto") { object flag; lock(state.Gate) { state.Prefs.autoClipboard=j.TryGetValue("enabled",out flag) && flag is bool && (bool)flag; state.SaveSettings(); } }
    else if(action=="peer-connect") { object flag; state.Peer.Connect(Request.Value(j,"url"),!j.TryGetValue("clipboard",out flag) || !(flag is bool) || (bool)flag,Request.Value(j,"code")); }
    else if(action=="peer-disconnect") state.Peer.Disconnect();
    else if(action=="peer-clipboard") { object flag; state.Peer.SetClipboardEnabled(j.TryGetValue("enabled",out flag) && flag is bool && (bool)flag); }
    else if(action=="peer-send") { string path=Request.Value(j,"path"); if(!BridgeState.Inside(path,state.Root)) throw new ApiError(403,"测试文件必须在隔离目录内"); state.Peer.SendFiles(new[]{path}); }
    else if(action=="peer-download") { var f=state.Peer.Files.FirstOrDefault(x=>x.id==Request.Value(j,"id")); if(f==null) throw new ApiError(404,"未找到对方文件"); state.Peer.Download(f); }
    else if(action=="target-file") { string path=Request.Value(j,"path"); if(!BridgeState.Inside(path,state.Root)) throw new ApiError(403,"测试文件必须在隔离目录内"); state.AddFileForDevice(path,Request.Value(j,"device")); }
    else if(action=="file-unack") { lock(state.Gate) { var f=state.Files.Find(x=>x.id==Request.Value(j,"id")); if(f!=null) { f.delivered=false; state.Revision++; } } }
    else if(action=="revoke") { lock(state.Gate) { state.Prefs.devices.Clear(); state.SaveSettings(); } }
    else if(action=="expire") { lock(state.Gate) { state.ClipTime=DateTime.UtcNow.AddMinutes(-11); state.PcTime=DateTime.UtcNow.AddMinutes(-11); } }
    else if(action=="age-pc") { lock(state.Gate) state.PcTime=DateTime.UtcNow.AddMinutes(-3); }
    else if(action=="add-file") { string path=Request.Value(j,"path"); if(!BridgeState.Inside(path,state.Root)) throw new ApiError(403,"测试文件必须在隔离目录内"); state.AddFile(path); }
    else throw new ApiError(400,"未知测试动作");
    lock(state.Gate) Json(r,new { ok=true,code=state.PairCode,pair=state.PairSecret,remote=state.RemoteOrigin,remoteCandidate=state.RemoteCandidateOrigin,remoteStatus=state.RemoteStatus,remoteVerified=state.RemoteVerified,remoteDiagnostic=state.RemoteDiagnostic,joins=state.Joins.Select(x=>new {key=x.key,check=x.check,name=x.name,kind=x.kind}).ToArray(),devices=state.Prefs.devices.Select(x=>new {id=x.id,name=x.name,kind=x.kind}).ToArray(),recent=state.GetRecentPeers(),peerConnected=state.Peer.Connected,peerStatus=state.Peer.Status,peerFiles=state.Peer.Files,peerFileStatus=state.Peer.FileStatus,peerClipboardStatus=state.Peer.ClipboardStatus,peerFileBusy=state.Peer.FileBusy,desktop=state.DesktopSnapshot(),attachment=state.LastAttachment,attachmentStatus=state.AttachmentStatus,files=state.Files.ToArray() }); return;
   }
   if(r.Method=="GET" && r.Path.StartsWith("/v1/download/")) { Download(r); return; }
   string auth=r.Header("Authorization"); if(!auth.StartsWith("Bearer ",StringComparison.Ordinal)) throw new ApiError(401,"请先连接电脑");
   string token=auth.Substring(7); Device device=state.Authenticate(token);
   if(remoteMode) { state.RemoteVerified=true; state.RemoteStatus="另一台设备已接入，可双向传输"; }
   if(device.kind=="computer") state.RetryAttachmentGroups("device:"+device.id,delegate {lock(state.Gate) return !state.Paused && state.Prefs.devices.Any(x=>x.id==device.id && x.hash==device.hash);});
   if(r.Method=="GET" && r.Path=="/v1/desktop/files") {
    if(device.kind!="computer") throw new ApiError(403,"此接口需要电脑配对");
    lock(state.Gate) { bool unchanged=r.Query("revision")==state.Revision.ToString(CultureInfo.InvariantCulture); Json(r,new {serverNow=BridgeState.NowMs(),revision=state.Revision,unchanged,files=unchanged?null:state.Files.Where(x=>state.VisibleFile(x,device)).OrderByDescending(x=>x.time).Select(x=>x.Public()).ToArray()}); } return;
   }
   if(r.Method=="GET" && r.Path=="/v1/state") {
    object[] files; lock(state.Gate) files=state.Files.Where(x=>state.VisibleFile(x,device)).OrderByDescending(x=>x.time).Select(x=>x.Public()).ToArray();
    Json(r,new { name=device.name,computer=Environment.MachineName,clipboard=state.ClipboardView(),files,revision=state.Revision, maxFile=remoteMode?90L*1024*1024:BridgeState.MaxFile,remote=remoteMode,syncInitialized=device.syncInitialized,autoClipboard=state.Prefs.autoClipboard }); return;
   }
   if(r.Method=="POST" && r.Path=="/v1/desktop/sync") {
    var d=r.Json(); object value; DesktopClip clip=null;
    if(d.TryGetValue("clip",out value) && value!=null) try { clip=BridgeState.Json().ConvertToType<DesktopClip>(value); } catch { throw new ApiError(400,"电脑同步数据无效"); }
    bool initialize=d.TryGetValue("initialize",out value) && value is bool && (bool)value;
    bool observeChanges=d.TryGetValue("observeChanges",out value) && value is bool && (bool)value;
    Json(r,state.DesktopExchange(device,Request.Value(d,"seen"),initialize,clip,observeChanges)); return;
   }
   if(r.Method=="POST" && r.Path.StartsWith("/v1/received/")) { state.AcknowledgeFile(r.Path.Substring("/v1/received/".Length),device); Json(r,new { ok=true }); return; }
   if(r.Method=="GET" && r.Path=="/v1/clipboard.txt") { Send(r.Stream,200,"text/plain; charset=utf-8",Encoding.UTF8.GetBytes(state.ClipboardText())); return; }
   if(r.Method=="POST" && r.Path=="/v1/clipboard") {
    var d=r.Json(); state.Authenticate(token); string text=Request.Value(d,"text"); state.PublishClipboard(text,device.name,true,device.id); state.RememberPhone(device,text); Json(r,new { ok=true }); return;
   }
   if(r.Method=="POST" && r.Path=="/v1/sync") { var d=r.Json(); state.Authenticate(token); Json(r,state.Exchange(device,Request.Value(d,"text"),Request.Value(d,"mode"))); return; }
   if(r.Method=="POST" && r.Path=="/v1/ack") { var d=r.Json(); state.Authenticate(token); state.Acknowledge(device,Request.Value(d,"receipt")); Json(r,new { ok=true }); return; }
   if(r.Method=="POST" && r.Path=="/v1/files") {
    if(remoteMode && r.Length>90L*1024*1024) throw new ApiError(413,"临时跨网单文件限 90 MB，大文件请同热点直传");
    if(!r.Headers.ContainsKey("Content-Length")) throw new ApiError(411,"需要文件长度");
    string name=r.Query("name"); if(name.Length==0) name="iPhone-file";
    if(r.Query("clipGroup")!="") {
     int index,count; long stamp;
     if(!Int32.TryParse(r.Query("clipIndex"),out index) || !Int32.TryParse(r.Query("clipCount"),out count) || !Int64.TryParse(r.Query("clipStamp"),out stamp)) throw new ApiError(400,"附件组信息无效");
     var metadata=new AttachmentInfo {group=r.Query("clipGroup"),kind=r.Query("clipKind"),index=index,count=count,stamp=stamp};
     var attachment=state.ReceiveAttachmentFile(r.Stream,r.Length,name,device,metadata,delegate {if(remoteMode && !RemoteActive()) throw new ApiError(403,"临时通道已关闭"); state.Authenticate(token);});
     Json(r,new {ok=true,file=attachment.Public(),clipboardStatus=state.AttachmentStatus}); return;
    }
    var f=state.ReceiveFile(r.Stream,r.Length,name,device.name,false,delegate { if(remoteMode && !RemoteActive()) throw new ApiError(403,"跨网连接已关闭"); state.Authenticate(token); },device.kind=="computer"?"@host":null); Json(r,f.Public()); return;
   }
   if(r.Method=="POST" && r.Path.StartsWith("/v1/tickets/")) {
    string id=r.Path.Substring("/v1/tickets/".Length);
    lock(state.Gate) if(!state.Files.Any(x=>x.id==id && state.VisibleFile(x,device))) throw new ApiError(404,"文件已移除或没有权限");
    string key=BridgeState.RandomToken(24);
    lock(tickets) {
     foreach(var old in tickets.Where(x=>x.Value.Until<DateTime.UtcNow).Select(x=>x.Key).ToArray()) tickets.Remove(old);
     if(tickets.Count>=100) throw new ApiError(429,"下载请求过多，请稍后重试");
     tickets[key]=new Ticket { FileId=id,DeviceHash=device.hash,Until=DateTime.UtcNow.AddMinutes(1) };
    }
    Json(r,new { url="/v1/download/"+id+"?ticket="+key }); return;
   }
   if(r.Method=="POST" && r.Path=="/v1/unpair") {
    lock(state.Gate) { state.Prefs.devices.RemoveAll(x=>x.id==device.id); state.SaveSettings(); } state.Changed(); Json(r,new{ok=true}); return;
   }
   throw new ApiError(404,"接口不存在");
  }
  void Download(Request r) {
   Ticket t; string ticket=r.Query("ticket"), id=r.Path.Substring("/v1/download/".Length);
   lock(tickets) { if(!tickets.TryGetValue(ticket,out t) || t.Until<DateTime.UtcNow || t.FileId!=id) throw new ApiError(403,"下载链接已过期，请重新点下载"); tickets.Remove(ticket); }
   Transfer f;
   lock(state.Gate) { if(state.Paused) throw new ApiError(503,"互传已暂停"); var device=state.Prefs.devices.Find(x=>x.hash==t.DeviceHash); if(device==null) throw new ApiError(401,"设备已取消配对"); f=state.Files.Find(x=>x.id==id && state.VisibleFile(x,device)); }
   if(f==null || !File.Exists(f.path)) throw new ApiError(404,"文件不存在");
   using(var file=new FileStream(f.path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
    Head(r.Stream,200,"application/octet-stream",file.Length,"Content-Disposition: attachment; filename=\"download\"; filename*=UTF-8''"+Uri.EscapeDataString(f.name)+"\r\n");
    byte[] buffer=new byte[65536]; int n;
    while((n=file.Read(buffer,0,buffer.Length))>0) {
     lock(state.Gate) if(state.Paused || (remoteMode && !RemoteActive()) || !state.Prefs.devices.Any(x=>x.hash==t.DeviceHash)) return;
     r.Stream.Write(buffer,0,n);
    }
   }
  }
  bool RemoteActive() { return running && state.RemoteOrigin!="" && DateTime.UtcNow<state.RemoteUntil; }
  bool CandidateActive() { return running && state.RemoteCandidateOrigin!="" && DateTime.UtcNow<state.RemoteCandidateUntil; }
  static void Json(Request r,object obj) { Send(r.Stream,200,"application/json; charset=utf-8",Encoding.UTF8.GetBytes(BridgeState.Json().Serialize(obj))); }
  static void TryError(Stream s,int status,string error) { try { Send(s,status,"application/json; charset=utf-8",Encoding.UTF8.GetBytes(BridgeState.Json().Serialize(new { error }))); } catch {} }
  static void Send(Stream s,int status,string type,byte[] body) { Head(s,status,type,body.Length,null); s.Write(body,0,body.Length); }
  static void Head(Stream s,int status,string type,long length,string extra) {
   string reason=status==200?"OK":status==401?"Unauthorized":status==404?"Not Found":status==403?"Forbidden":"Error";
   string header="HTTP/1.1 "+status+" "+reason+"\r\nContent-Type: "+type+"\r\nContent-Length: "+length.ToString(CultureInfo.InvariantCulture)+"\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'\r\n"+(extra??"")+"\r\n";
   byte[] b=Encoding.ASCII.GetBytes(header); s.Write(b,0,b.Length);
  }
 }
}
