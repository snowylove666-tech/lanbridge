using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LanBridge {
 public sealed class PeerFile { public string id,name,direction; public long size; public bool targeted,delivered; public AttachmentInfo attachment; }
 public sealed class PeerClient : IDisposable {
  readonly BridgeState state; readonly object gate=new object();
  readonly HashSet<HttpWebRequest> requests=new HashSet<HttpWebRequest>();
  readonly HashSet<string> downloading=new HashSet<string>();
  long generation,clipboardEpoch,remoteClockOffset; PeerSettings active; bool connected;
  readonly Queue<PendingDownload> downloadQueue=new Queue<PendingDownload>();
  bool downloadWorker; int uploads;
  sealed class PendingDownload { public PeerFile file; public bool acknowledge; }
  public string Status="尚未连接另一台电脑", FileStatus="", ClipboardStatus="等待连接电脑";
  public bool FileBusy { get { lock(gate) return uploads>0 || downloading.Count>0; } }
  public PeerFile[] Files=new PeerFile[0]; public bool Connected { get { lock(gate) return connected; } }
  public string Name { get { lock(gate) return active==null?"另一台电脑":active.name; } }
  public PeerClient(BridgeState state) { this.state=state; state.Peer=this; }
  public static string Normalize(string text) {
   text=(text??"").Trim(); if(!text.Contains("://")) text="http://"+text;
   Uri uri; if(!Uri.TryCreate(text,UriKind.Absolute,out uri) || (uri.Scheme!="http" && uri.Scheme!="https") || uri.UserInfo!="" || uri.AbsolutePath!="/" || uri.Query!="") throw new ApiError(400,"请填写另一台轻桥的完整连接地址");
   if(uri.Scheme=="http") { IPAddress ip; if(!(uri.Host=="localhost" || uri.Host.EndsWith(".local",StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(uri.Host,out ip) && BridgeState.PrivateAddress(ip)))) throw new ApiError(400,"HTTP 只允许可信局域网地址，跨网请用 HTTPS 链接"); }
   return uri.GetLeftPart(UriPartial.Authority);
  }
  public static string ValidateAddress(string address) {
   address=(address??"").Trim(); string url;
   try { url=Normalize(address); }
   catch(ApiError) {
    if(address.IndexOf("https://",StringComparison.OrdinalIgnoreCase)<0) throw;
    try { return ConnectionInvitation.Parse(address); } catch(FormatException ex) { throw new ApiError(400,ex.Message); }
   }
   Uri original=new Uri(address.Contains("://")?address:"http://"+address);
   if(original.Fragment=="") return url; // Saved remote credentials reconnect without a new invitation.
   if(original.Scheme=="https") { try { return ConnectionInvitation.Parse(address); } catch(FormatException ex) { throw new ApiError(400,ex.Message); } }
   if(!original.Fragment.StartsWith("#pair=",StringComparison.Ordinal) || !Regex.IsMatch(original.Fragment.Substring(6),@"\A(?:[a-f0-9]{32}|[0-9]{6})\z")) throw new ApiError(400,"链接格式不完整，请重新复制对方的完整连接地址");
   return url+"/"+original.Fragment;
  }
  bool Current(long id) { lock(gate) return generation==id; }
  void Update(long id,string text) { lock(gate) { if(generation!=id) return; Status=text; } state.Changed(); }
  void FileUpdate(long id,string text) { lock(gate) { if(generation!=id) return; FileStatus=text; } state.Changed(); }
  void ClipboardUpdate(long id,string text) { lock(gate) { if(generation!=id) return; ClipboardStatus=text; } state.Changed(); }
  long Reset() {
   lock(gate) { generation++; connected=false; active=null; Files=new PeerFile[0]; FileStatus=""; ClipboardStatus="等待连接电脑"; downloading.Clear(); downloadQueue.Clear(); downloadWorker=false; uploads=0; foreach(var r in requests.ToArray()) try { r.Abort(); } catch {} requests.Clear(); return generation; }
  }
  public void SetClipboardEnabled(bool enabled) {
   lock(state.Gate) { if(state.Prefs.peer!=null) state.Prefs.peer.clipboard=enabled; state.SaveSettings(); }
   lock(gate) { if(active!=null) active.clipboard=enabled; clipboardEpoch++; ClipboardStatus=enabled?"已开启，等待新复制的文字":"电脑文字自动同步已关闭"; }
   state.Changed();
  }
  public void Connect(string address,bool clipboard,string code="") {
   if(DeviceInvitation.IsBundle(address)) { ConnectBundle(address,clipboard); return; }
   address=ValidateAddress(address);
   string url=Normalize(address); Uri original=new Uri(address.Contains("://")?address:"http://"+address);
   if(code=="" && original.Fragment.StartsWith("#pair=")) code=Uri.UnescapeDataString(original.Fragment.Substring(6));
   if(original.Fragment!="" && !original.Fragment.StartsWith("#pair=")) throw new ApiError(400,"链接格式不完整，请复制对方轻桥中的完整配对链接");
   if(code!="" && !Regex.IsMatch(code,@"\A(?:[a-f0-9]{32}|[0-9]{6})\z")) throw new ApiError(400,"配对密钥不完整，请重新复制完整链接");
   string initialLocal=state.DesktopSnapshot().id;
   long id=Reset(); string pairing=code;
   Update(id,"正在连接…");
   new Thread(delegate() { Join(id,url,clipboard,pairing,initialLocal); }) { IsBackground=true,Name="Computer pairing" }.Start();
  }
  void ConnectBundle(string text,bool clipboard) {
   var invite=DeviceInvitation.Parse(text); string initial=state.DesktopSnapshot().id; long id=Reset(); Update(id,"正在尝试直连这台电脑…");
   new Thread(delegate() {
    try {
     foreach(string address in invite.addresses.Where(x=>x.StartsWith("http://"))) {
      if(!Current(id)) return;
      try { var info=Json(id,address,"/v1/connection",null,null,1400); if(Str(info,"serverId")==invite.device) {Join(id,address,clipboard,invite.secret,initial);return;} } catch {}
     }
     string remote=invite.addresses.FirstOrDefault(x=>x.StartsWith("https://"));
     if(remote!=null) { Update(id,"直连未成功，正在尝试邀请中的互联网入口…"); var info=Json(id,remote,"/v1/connection",null,null); if(Str(info,"serverId")!=invite.device) throw new ApiError(403,"邀请入口不是原来的电脑，请重新获取邀请"); Join(id,remote,clipboard,invite.secret,initial); return; }
     Update(id,"暂未连通。可让另一台电脑用本机邀请主动加入，或待互联网入口准备好后更新邀请。");
    } catch(Exception ex) {Update(id,"未连接："+ex.Message);}
   }) {IsBackground=true,Name="Automatic connection route"}.Start();
  }
  public void Resume() { PeerSettings saved; lock(state.Gate) saved=state.Prefs.peer; if(saved!=null && saved.enabled) Connect(saved.url,saved.clipboard); }
  public void Disconnect() { Reset(); lock(state.Gate) { if(state.Prefs.peer!=null) state.Prefs.peer.enabled=false; state.SaveSettings(); } Status="已断开，暂停电脑间自动同步"; state.Changed(); }
  static bool Retryable(Exception ex) { var api=ex as ApiError; return ex is IOException || (api!=null && (api.Status==408 || api.Status==429 || api.Status==502 || api.Status==503 || api.Status==504)); }
  Dictionary<string,object> WaitJson(long id,string url,string path,object body,string token,DateTime until) {
   while(Current(id)) {
    try { return Json(id,url,path,body,token); }
    catch(Exception ex) {
     if(!Retryable(ex) || DateTime.UtcNow>=until) throw;
     Update(id,"暂时无法连接，正在自动重试："+ex.Message+(new Uri(url).Scheme=="https"?"。临时跨网到期或重开后，需对方提供新链接":"")); Thread.Sleep(2000);
    }
   } throw new OperationCanceledException("已取消连接");
  }
  void Join(long id,string url,bool clipboard,string code,string initialLocal) {
   try {
    var info=WaitJson(id,url,"/v1/connection",null,null,DateTime.MaxValue);
    if(!Bool(info,"desktop")) throw new ApiError(409,"另一台电脑需要升级到轻桥 1.3 或更高版本");
    string serverId=Str(info,"serverId"); if(serverId==state.Prefs.installId) throw new ApiError(400,"这是本机地址，请选择另一台电脑");
    PeerSettings saved=state.FindRememberedPeer(serverId,url);
    string token=saved!=null && saved.serverId==serverId && (code=="" || saved.url==url)?saved.token:"";
    if(token!="") try { WaitJson(id,url,"/v1/state",null,token,DateTime.MaxValue); } catch(ApiError ex) { if(ex.Status==401) token=""; else throw; }
    if(token=="") {
     Dictionary<string,object> result;
     if(code!="") {
      string requestId=BridgeState.RandomToken(16);
      try { result=WaitJson(id,url,"/v1/pair",new { code,name=Environment.MachineName,kind="computer",requestId },null,DateTime.UtcNow.AddSeconds(45)); }
      catch(ApiError ex) { if(ex.Status==403) throw new ApiError(403,"邀请已使用、已刷新或超过 5 分钟，请对方重新复制连接邀请"); throw; }
     }
     else {
      if(Bool(info,"remote")) throw new ApiError(403,"跨网请粘贴对方复制的完整配对链接（含 #pair=），不需要电脑扫码");
      var join=Json(id,url,"/v1/join",new { name=Environment.MachineName,kind="computer" },null);
      Update(id,"请在对方电脑允许连接 · 识别号 "+Str(join,"check")); result=null;
      DateTime until=DateTime.UtcNow.AddMinutes(2);
      while(Current(id) && DateTime.UtcNow<until) { Thread.Sleep(700); result=WaitJson(id,url,"/v1/join/poll",new {key=Str(join,"key")},null,until); if(!Bool(result,"pending")) break; }
      if(!Current(id)) return; if(result==null || Bool(result,"pending")) throw new ApiError(403,"等待授权超时，请重新连接");
     }
     token=Str(result,"token"); if(!Regex.IsMatch(token,@"\A[a-f0-9]{64}\z")) throw new ApiError(400,"对方返回了无效凭据");
    }
    if(!Current(id)) return;
    var settings=new PeerSettings {url=url,token=token,serverId=serverId,name=Str(info,"computer"),clipboard=clipboard};
    lock(state.Gate) { if(!Current(id)) return; state.Prefs.peer=settings; if(clipboard) state.Prefs.autoClipboard=true; state.SaveSettings(); }
    lock(gate) { if(!Current(id)) return; active=settings; }
    Loop(id,settings,initialLocal,Str(info,"clipboardEventId"),Bool(info,"desktopObserveChanges"));
   } catch(Exception ex) { Update(id,"未连接："+ex.Message); lock(gate) { if(Current(id)) connected=false; } }
  }
  void Loop(long id,PeerSettings peer,string initialLocal,string initialRemote,bool observeChanges) {
   string sent=initialLocal,seen=initialRemote; bool initialized=false; long offset=0,bestRtt=Int64.MaxValue,fileRevision=-1,observedEpoch;
   bool remembered=false;
   lock(gate) observedEpoch=clipboardEpoch;
   ClipboardUpdate(id,peer.clipboard?"已开启，等待新复制的文字":"电脑文字自动同步已关闭");
   while(Current(id)) {
    try {
     if(state.Paused) { lock(gate) if(Current(id)) connected=false; Update(id,"本机互传已暂停"); Thread.Sleep(800); continue; }
     long epoch; lock(gate) epoch=clipboardEpoch;
     if(epoch!=observedEpoch) { sent=state.DesktopSnapshot().id; seen=""; initialized=false; observeChanges=false; observedEpoch=epoch; }
     bool remoteAuto=true;
     if(peer.clipboard) {
      var local=state.DesktopSnapshot(); long start=BridgeState.NowMs();
      DesktopClip outgoing=initialized && local.id!=sent && local.text!="" && start-local.stamp<=120000 ? new DesktopClip {id=local.id,text=local.text,source=Environment.MachineName,stamp=local.stamp+offset} : null;
      var result=Json(id,peer.url,"/v1/desktop/sync",new {initialize=!initialized,observeChanges,seen,clip=outgoing},peer.token);
      if(!Current(id)) return;
      long end=BridgeState.NowMs(),rtt=Math.Max(0,end-start);
      if(rtt<bestRtt) { bestRtt=rtt; offset=Number(result,"serverNow")-(start+end)/2; lock(gate) remoteClockOffset=offset; }
      if(!initialized) { if(!observeChanges) seen=Str(result,"currentId"); initialized=true; }
      if(outgoing!=null && Bool(result,"autoClipboard")) sent=outgoing.id;
      if(outgoing!=null && Bool(result,"accepted")) ClipboardUpdate(id,"新复制的文字已送达 "+peer.name+" · "+DateTime.Now.ToString("HH:mm:ss"));
      object raw; if(result.TryGetValue("clip",out raw) && raw!=null) {
       var incoming=BridgeState.Json().ConvertToType<DesktopClip>(raw); string remoteId=incoming.id; incoming.stamp-=offset; long receivedEpoch=epoch; incoming.allow=delegate { lock(gate) return generation==id && peer.clipboard && clipboardEpoch==receivedEpoch; };
       int applied=state.ApplyDesktop(incoming);
       if(applied==0 && state.Paused) throw new ApiError(503,"本机互传已暂停，文字未写入");
       if(applied>=0) { seen=remoteId; if(applied>0) sent=remoteId; }
       if(applied>0) ClipboardUpdate(id,"已收到 "+peer.name+" 的文字，可直接粘贴 · "+DateTime.Now.ToString("HH:mm:ss"));
       else if(applied<0) ClipboardUpdate(id,"本机剪贴板暂时被占用，正在自动重试");
      }
      remoteAuto=Bool(result,"autoClipboard");
      if(!remoteAuto) ClipboardUpdate(id,"对方尚未开启自动共享文字；文件连接正常");
      else if(ClipboardStatus.StartsWith("对方尚未开启")) ClipboardUpdate(id,"两端已开启，等待新复制的文字");
     }
     long fileStart=BridgeState.NowMs(); var snapshot=Json(id,peer.url,"/v1/desktop/files?revision="+fileRevision,null,peer.token); object list;
     if(!peer.clipboard && Number(snapshot,"serverNow")>0) {lock(gate) remoteClockOffset=Number(snapshot,"serverNow")-(fileStart+BridgeState.NowMs())/2;}
     var files=Bool(snapshot,"unchanged")?Files:snapshot.TryGetValue("files",out list) && list is System.Collections.IEnumerable ? ((System.Collections.IEnumerable)list).Cast<object>().Select(x=>BridgeState.Json().ConvertToType<PeerFile>(x)).Where(x=>x.direction=="toPhone").ToArray() : new PeerFile[0];
     fileRevision=Number(snapshot,"revision");
     lock(gate) { if(!Current(id)) return; Files=files; connected=true; }
     if(!remembered) { try { state.RememberPeer(peer); } catch {} remembered=true; }
     foreach(var file in files.Where(x=>x.targeted && !x.delivered)) QueueDownload(id,peer,file,true);
     state.RetryAttachmentGroups("peer:"+peer.serverId,delegate { return Current(id) && !state.Paused; });
     Update(id,"已连接 "+peer.name);
     Thread.Sleep(800);
    } catch(Exception ex) {
     lock(gate) { if(Current(id)) connected=false; }
     if(ex is ApiError && ((ApiError)ex).Status==401) { Update(id,"配对已失效，请重新连接；临时跨网需新配对链接"); ClipboardUpdate(id,"连接已失效，文字尚未同步"); return; }
     ClipboardUpdate(id,"文字同步待恢复："+ex.Message);
     Update(id,"连接中断，稍后自动重试："+ex.Message); Thread.Sleep(2000);
    }
   }
  }
  HttpWebRequest Request(long id,string url,string path,string token,string method) {
   if(!Current(id)) throw new OperationCanceledException("已断开");
   var request=(HttpWebRequest)WebRequest.Create(url+path); request.Method=method; request.AllowAutoRedirect=false; request.Proxy=null;
   if(new Uri(url).Scheme=="https") request.Proxy=WebRequest.DefaultWebProxy;
   request.Timeout=new Uri(url).Scheme=="https"?15000:8000; request.ReadWriteTimeout=60000; request.KeepAlive=new Uri(url).Scheme=="https"; request.UserAgent="LanBridge/1.5";
   request.ServicePoint.ConnectionLimit=8;
   if(token!=null) request.Headers["Authorization"]="Bearer "+token;
   ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
   lock(gate) { if(!Current(id)) throw new OperationCanceledException(); requests.Add(request); }
   return request;
  }
  static string ReadText(Stream stream) { using(var memory=new MemoryStream()) { byte[] b=new byte[8192]; int n; while((n=stream.Read(b,0,b.Length))>0) { if(memory.Length+n>2097152) throw new ApiError(413,"响应过大"); memory.Write(b,0,n); } return Encoding.UTF8.GetString(memory.ToArray()); } }
  Dictionary<string,object> Json(long id,string url,string path,object body,string token,int timeout=0) {
   var request=Request(id,url,path,token,body==null?"GET":"POST");
   if(timeout>0) {request.Timeout=timeout;request.ReadWriteTimeout=timeout;}
   try {
    if(body!=null) { byte[] bytes=Encoding.UTF8.GetBytes(BridgeState.Json().Serialize(body)); request.ContentType="application/json"; request.ContentLength=bytes.Length; using(var stream=request.GetRequestStream()) stream.Write(bytes,0,bytes.Length); }
    using(var response=(HttpWebResponse)request.GetResponse()) {
     if((int)response.StatusCode!=200) throw new ApiError((int)response.StatusCode,"对方返回了无效响应");
     using(var stream=response.GetResponseStream()) return BridgeState.Json().Deserialize<Dictionary<string,object>>(ReadText(stream));
    }
   } catch(WebException ex) { throw Error(ex); } finally { lock(gate) requests.Remove(request); }
  }
  static Exception Error(WebException ex) {
   var response=ex.Response as HttpWebResponse;
   if(response==null) {
    string reason=ex.Status==WebExceptionStatus.NameResolutionFailure || ex.Status==WebExceptionStatus.ProxyNameResolutionFailure?"地址解析失败，请检查网络与链接；跨网临时地址重开后会改变":ex.Status==WebExceptionStatus.Timeout?"连接超时，请确认对方轻桥仍在运行且跨网通道未关闭":ex.Status==WebExceptionStatus.TrustFailure || ex.Status==WebExceptionStatus.SecureChannelFailure?"HTTPS 安全连接失败，请检查系统时间、证书或网络代理":ex.Status==WebExceptionStatus.RequestCanceled?"连接请求已取消":"无法连接，请检查地址、网络和对方程序（"+ex.Status+"）";
    return new IOException(reason,ex);
   }
   using(response) { try { using(var stream=response.GetResponseStream()) { var data=BridgeState.Json().Deserialize<Dictionary<string,object>>(ReadText(stream)); string message=Str(data,"error"); if(message=="") throw new IOException(); return new ApiError((int)response.StatusCode,message); } } catch { return new ApiError((int)response.StatusCode,"对方入口返回 HTTP "+(int)response.StatusCode+"，可能通道未就绪、已关闭或被网络代理拦截"); } }
  }
  static string Str(Dictionary<string,object> d,string key) { object v; return d!=null && d.TryGetValue(key,out v)?Convert.ToString(v):""; }
  static bool Bool(Dictionary<string,object> d,string key) { object v; return d!=null && d.TryGetValue(key,out v) && v is bool && (bool)v; }
  static long Number(Dictionary<string,object> d,string key) { object v; return d!=null && d.TryGetValue(key,out v)?Convert.ToInt64(v):0; }
  public void SendFiles(string[] paths) {
   PeerSettings peer; long id; lock(gate) { peer=active; id=generation; if(peer==null || !connected) throw new ApiError(409,"请先连接另一台电脑"); uploads++; }
   new Thread(delegate() { int completed=0; var failures=new List<string>(); try { foreach(string path in paths) { if(!Current(id)) return; try { Upload(id,peer,path); completed++; } catch(Exception ex) { failures.Add(Path.GetFileName(path)+"："+ex.Message); FileUpdate(id,"发送未确认："+Path.GetFileName(path)+" · "+ex.Message); } } if(failures.Count>0) FileUpdate(id,"发送结果：对方已保存 "+completed+" 个，未确认 "+failures.Count+" 个。"+String.Join("；",failures.ToArray())+"。未确认文件请先查看对方接收记录，再决定是否重试"); else if(paths.Length>1) FileUpdate(id,"发送完成：对方已保存全部 "+completed+" 个文件"); } finally { lock(gate) if(Current(id)) uploads--; } }) { IsBackground=true,Name="Computer file upload" }.Start();
  }
  public void SendClipboardFiles(string[] paths,string kind,AttachmentClip localEvent=null,Action finished=null) {
   var clip=state.PrepareAttachment(paths,kind,localEvent); PeerSettings peer; long id,offset;
   lock(gate) { peer=active; id=generation; offset=remoteClockOffset; if(peer==null || !connected) throw new ApiError(409,"请先连接另一台电脑"); uploads++; }
   new Thread(delegate() { try {
    if(!Bool(Json(id,peer.url,"/v1/connection",null,null),"clipboardAttachments")) throw new ApiError(409,"对方需要升级到轻桥 1.5，才能将图片／文件直接放入剪贴板");
    for(int i=0;i<clip.paths.Length;i++) { if(!Current(id)) return; Upload(id,peer,clip.paths[i],new AttachmentInfo {group=clip.id,kind=kind,index=i,count=clip.paths.Length,stamp=clip.stamp+offset}); }
   } catch(Exception ex) { FileUpdate(id,"图片／文件发送未完成："+ex.Message+"。已接收的文件保留，完整收齐前不改剪贴板"); }
   finally { lock(gate) if(Current(id)) uploads--; if(finished!=null) finished(); } }) {IsBackground=true,Name="Clipboard attachment upload"}.Start();
  }
  void Upload(long id,PeerSettings peer,string path,AttachmentInfo attachment=null) {
   var info=new FileInfo(path); if(!info.Exists) throw new IOException("文件不存在；文件夹请先压缩为 ZIP");
   long limit=Number(Json(id,peer.url,"/v1/connection",null,null),"maxFile"); if(info.Length>limit) throw new ApiError(413,"文件超过对方当前连接允许的大小");
   string route="/v1/files?name="+Uri.EscapeDataString(info.Name);
   if(attachment!=null) route+="&clipGroup="+attachment.group+"&clipKind="+attachment.kind+"&clipIndex="+attachment.index+"&clipCount="+attachment.count+"&clipStamp="+attachment.stamp;
   var request=Request(id,peer.url,route,peer.token,"POST"); request.AllowWriteStreamBuffering=false; request.ContentType="application/octet-stream"; request.ContentLength=info.Length;
   try {
    using(var file=info.OpenRead()) using(var stream=request.GetRequestStream()) {
     byte[] bytes=new byte[65536]; int n; long total=0; DateTime progress=DateTime.MinValue;
     while((n=file.Read(bytes,0,bytes.Length))>0) { if(!Current(id) || state.Paused) throw new OperationCanceledException("传输已暂停或断开"); stream.Write(bytes,0,n); total+=n; if(DateTime.UtcNow-progress>TimeSpan.FromMilliseconds(250)) { FileUpdate(id,"正在发送 "+info.Name+" · "+(info.Length==0?100:total*100/info.Length)+"%"); progress=DateTime.UtcNow; } }
    }
    using(var response=(HttpWebResponse)request.GetResponse()) { if((int)response.StatusCode!=200) throw new IOException("对方未确认接收"); using(var stream=response.GetResponseStream()) { string reply=ReadText(stream); if(attachment!=null) { var data=BridgeState.Json().Deserialize<Dictionary<string,object>>(reply); FileUpdate(id,Str(data,"clipboardStatus")); } } }
    if(attachment==null) FileUpdate(id,"对方已保存到 Received · "+peer.name+"："+info.Name);
   } catch(WebException ex) { throw Error(ex); } finally { lock(gate) requests.Remove(request); }
  }
  public void Download(PeerFile file) { PeerSettings peer; long id; lock(gate) { peer=active; id=generation; if(peer==null || !connected) throw new ApiError(409,"请先连接"); } QueueDownload(id,peer,file,file.targeted); }
  void QueueDownload(long id,PeerSettings peer,PeerFile file,bool acknowledge) {
   bool start=false;
   lock(gate) { if(!Current(id) || downloading.Contains(file.id)) return; downloading.Add(file.id); downloadQueue.Enqueue(new PendingDownload {file=file,acknowledge=acknowledge}); if(!downloadWorker) {downloadWorker=true;start=true;} }
   FileUpdate(id,"接收队列 · "+file.name);
   if(start) new Thread(delegate() { DownloadLoop(id,peer); }) {IsBackground=true,Name="Computer file download"}.Start();
  }
  void DownloadLoop(long id,PeerSettings peer) {
   while(Current(id)) {
    PendingDownload next;
    lock(gate) { if(!Current(id)) return; if(downloadQueue.Count==0) {downloadWorker=false;return;} next=downloadQueue.Dequeue(); }
    PeerFile file=next.file; bool saved=false;
    try {
     string key=BridgeState.Hash(peer.serverId+"|"+file.id);
     if(!state.HasReceived(key)) {
      var ticket=Json(id,peer.url,"/v1/tickets/"+Uri.EscapeDataString(file.id),new {},peer.token); string path=Str(ticket,"url");
      if(!Regex.IsMatch(path,@"\A/v1/download/[a-f0-9]{24}\?ticket=[a-f0-9]{48}\z")) throw new ApiError(400,"下载地址无效");
      var request=Request(id,peer.url,path,null,"GET");
      try { using(var response=(HttpWebResponse)request.GetResponse()) using(var stream=response.GetResponseStream()) {
       if(response.ContentLength!=file.size) throw new IOException("文件长度不匹配");
       FileUpdate(id,"正在接收 "+file.name);
       AttachmentInfo metadata=file.attachment==null?null:file.attachment.Copy();
       if(metadata!=null) { long delta; lock(gate) delta=remoteClockOffset; metadata.stamp-=delta; lock(state.Gate) { var prior=state.Files.FirstOrDefault(x=>x.attachmentOwner=="peer:"+peer.serverId && x.attachment!=null && x.attachment.group==metadata.group); if(prior!=null) metadata.stamp=prior.attachment.stamp; } }
       state.ReceiveFile(stream,response.ContentLength,file.name,peer.name,false,delegate { if(!Current(id) || state.Paused) throw new OperationCanceledException("接收已中断"); },"@host",key,metadata,metadata==null?null:"peer:"+peer.serverId);
      } } catch(WebException ex) { throw Error(ex); } finally { lock(gate) requests.Remove(request); }
     }
     saved=true;
     if(next.acknowledge) Json(id,peer.url,"/v1/received/"+file.id,new {},peer.token);
     if(file.attachment!=null) { var applied=state.TryApplyAttachment("peer:"+peer.serverId,file.attachment.group,delegate {return Current(id) && !state.Paused;}); FileUpdate(id,applied.status); }
     else FileUpdate(id,"已保存到 Received："+file.name);
    } catch(Exception ex) { FileUpdate(id,saved?"已保存到 Received，接收确认待重试："+file.name:"接收失败："+file.name+" · "+ex.Message); Thread.Sleep(1200); }
    finally { lock(gate) if(Current(id)) downloading.Remove(file.id); }
   }
  }
  public void Dispose() { Reset(); }
 }
}
