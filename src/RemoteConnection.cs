using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LanBridge {
 // Optional transport, downloaded only after the user enables it. The main
 // listener never trusts proxy headers; remote traffic uses a separate loopback listener.
 public sealed class RemoteConnection : IDisposable {
  public const string Version="2026.10.0";
  const string Digest="86aee4017b26625cee8484c113558f48effa4cd47f7aa05fcf425604e5d2b23c";
  readonly BridgeState state; readonly object gate=new object();
  Process child; BridgeServer server; WebClient download; int generation; bool active;
  readonly System.Threading.Timer expiry;
  public RemoteConnection(BridgeState state) { this.state=state; state.ToggleRemote=Toggle; expiry=new System.Threading.Timer(delegate { if(state.RemoteOrigin!="" && DateTime.UtcNow>=state.RemoteUntil) Stop("跨网连接已到期，已自动关闭"); },null,1000,1000); }
  public void Toggle() { lock(gate) { if(active) { Stop("跨网连接已关闭"); return; } active=true; state.RemoteRunning=true; int id=++generation; state.RemoteStatus="准备跨网模块…（可再次点击取消）"; state.Changed(); new Thread(delegate() { Start(id); }) { IsBackground=true,Name="Optional remote transport" }.Start(); } }
  bool Current(int id) { lock(gate) return active && generation==id; }
  void Status(int id,string text) { if(Current(id)) { state.RemoteStatus=text; state.Changed(); } }
  static string FileHash(string path) { using(var s=File.OpenRead(path)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(s)).Replace("-","").ToLowerInvariant(); }
  void Start(int id,string edgeArgs=null) {
   string temp=null; Process process=null; BridgeServer remote=null; ChildJob job=null;
   bool retryDns=false;
   try {
    if(!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("临时跨网模块需要 64 位 Windows；局域网功能仍可使用");
    string folder=Path.Combine(state.Root,"Data","Tools"); Directory.CreateDirectory(folder);
    string exe=Path.Combine(folder,"cloudflared-"+Version+".exe");
    if(!File.Exists(exe) || FileHash(exe)!=Digest) {
     Status(id,"首次下载跨网模块，完成后创建临时入口；再次点击可取消");
     temp=exe+"."+id+".part";
     using(var web=new WebClient()) {
      lock(gate) { if(!Current(id)) return; download=web; }
      ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
      web.Headers["User-Agent"]="LanBridge/1.5";
      web.DownloadFileTaskAsync(new Uri("https://github.com/cloudflare/cloudflared/releases/download/"+Version+"/cloudflared-windows-amd64.exe"),temp).GetAwaiter().GetResult();
      lock(gate) { if(download==web) download=null; }
     }
     if(!Current(id)) return;
     if(FileHash(temp)!=Digest) throw new InvalidOperationException("跨网模块 SHA-256 校验失败，已拒绝运行");
     lock(gate) { if(!Current(id)) return; if(File.Exists(exe)) File.Delete(exe); File.Move(temp,exe); temp=null; }
    }
    if(!Current(id)) return;
    string config=Path.Combine(folder,"quick-tunnel.yml"); File.WriteAllText(config,"{}\n");
    remote=new BridgeServer(state,null,true); remote.Start();
    string origin="",lastError=""; bool connected=false;
    process=new Process { StartInfo=new ProcessStartInfo(exe,"tunnel --config \""+config+"\" --no-autoupdate --url http://127.0.0.1:"+remote.ListeningPort+(edgeArgs??"")) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true,RedirectStandardOutput=true,WorkingDirectory=folder } };
    foreach(string key in process.StartInfo.EnvironmentVariables.Keys.Cast<string>().Where(x=>x.StartsWith("TUNNEL_",StringComparison.OrdinalIgnoreCase)).ToArray()) process.StartInfo.EnvironmentVariables.Remove(key);
    process.StartInfo.EnvironmentVariables["TUNNEL_TRANSPORT_PROTOCOL"]="http2";
    DataReceivedEventHandler read=delegate(object sender,DataReceivedEventArgs e) {
     if(e.Data==null || !Current(id)) return;
     var match=Regex.Match(e.Data,@"https://[a-z0-9-]+\.trycloudflare\.com\b",RegexOptions.IgnoreCase);
     lock(gate) { if(match.Success) origin=match.Value.ToLowerInvariant(); if(e.Data.Contains("Registered tunnel connection")) connected=true; if(e.Data.Contains("ERR") || e.Data.Contains("error") || e.Data.Contains("Incorrect Usage")) lastError=e.Data.Substring(0,Math.Min(300,e.Data.Length)); }
    };
    process.OutputDataReceived+=read; process.ErrorDataReceived+=read;
    lock(gate) { if(!Current(id)) return; child=process; server=remote; process.Start(); job=new ChildJob(process); process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
    Status(id,"正在建立临时 HTTPS 通道…");
    DateTime deadline=DateTime.UtcNow.AddSeconds(60);
    while(Current(id) && DateTime.UtcNow<deadline && !process.HasExited) {
     bool ready; lock(gate) ready=origin!="" && connected;
     if(ready) break; Thread.Sleep(200);
    }
    if(!Current(id)) return;
    lock(gate) {
     if(!Current(id)) return;
     if(origin=="" || !connected || process.HasExited) throw new InvalidOperationException("临时通道连接失败，请检查网络后重试；局域网仍可使用。"+lastError);
     if(origin.Length+7+32>106) throw new InvalidOperationException("本次临时地址过长，请关闭后重试");
     state.RemoteCandidateOrigin=origin; state.RemoteCandidateUntil=DateTime.UtcNow.AddMinutes(2);
    }
    lock(gate) {
     if(!Current(id)) return;
     if(DateTime.UtcNow>=state.PairUntil || String.IsNullOrEmpty(state.PairSecret)) state.RefreshPairing(); state.RemoteUntil=DateTime.UtcNow.AddMinutes(30); state.RemoteOrigin=origin;
     state.RemoteVerified=false; state.RemoteDiagnostic=""; state.RemoteCandidateOrigin=""; state.RemoteCandidateUntil=DateTime.MinValue;
    }
    Status(id,"临时入口已创建，等待对方加入；正在后台检查连接…");
    string verificationError=""; bool verified=false;
    for(int attempt=0;attempt<8 && Current(id) && !process.HasExited;attempt++) {
     if(state.RemoteVerified) {verified=true;break;}
     try { VerifyPublicRoute(origin,state.Prefs.installId); verified=true; break; }
     catch(Exception ex) {
      verificationError=ex.Message; state.RemoteDiagnostic=verificationError;
      Status(id,"临时入口已创建，可复制邀请；后台自动检查 "+(attempt+1)+"/8，尚未确认对方已连接");
      int delay=Math.Min(10000,1000*(attempt+1));
      for(int elapsed=0;elapsed<delay && Current(id) && !state.RemoteVerified;elapsed+=200) Thread.Sleep(200);
     }
    }
    if(!Current(id)) return;
    if(verified || state.RemoteVerified) { state.RemoteVerified=true; bool hasDevice; lock(state.Gate) hasDevice=state.Prefs.devices.Any(x=>x.temporary); Status(id,hasDevice?"已有设备加入，可双向传输":"本机检测已通过，等待对方加入"); }
    else { state.RemoteDiagnostic=verificationError; Status(id,"本机公网自测未通过，可让另一台设备尝试邀请；尚未确认连接"); }
    while(Current(id) && !process.HasExited) Thread.Sleep(500);
    if(Current(id)) Stop("跨网通道已断开，请重新开启，并向对方提供新的配对链接");
   } catch(Exception ex) {
    if(Current(id)) {
     if(edgeArgs==null && (ex.Message.Contains("DNS") || ex.Message.Contains("lookup"))) retryDns=true;
     else Stop("跨网未开启："+ex.Message);
    }
   }
   finally {
    if(temp!=null) try { File.Delete(temp); } catch {}
    if(remote!=null) remote.Dispose();
    if(job!=null) job.Dispose();
    if(process!=null) { try { if(!process.HasExited) process.Kill(); } catch {} process.Dispose(); }
   }
   if(retryDns && Current(id)) {
    Status(id,"系统 DNS 不兼容，正在通过阿里 DNS 解析公开隧道地址…");
    try { string resolved=TunnelDns.Resolve(); if(Current(id)) Start(id,resolved); }
    catch(Exception ex) { if(Current(id)) Stop("跨网未开启：兼容解析失败。"+ex.Message); }
   }
  }
  // Registration confirms an edge connection, not that the new public hostname
  // resolves or routes back here. A fresh nonce and install identity prevent an
  // error page, proxy response or stale route from being reported as success.
  internal static void VerifyPublicRoute(string origin,string instance) {
   try { VerifyPublicRouteUsing(origin,instance,false); }
   catch(IOException) { VerifyPublicRouteUsing(origin,instance,true); }
  }
  static void VerifyPublicRouteUsing(string origin,string instance,bool direct) {
   string nonce=BridgeState.RandomToken(16);
   var request=(HttpWebRequest)WebRequest.Create(origin+"/health?nonce="+nonce);
   request.Timeout=8000; request.ReadWriteTimeout=8000; request.AllowAutoRedirect=false;
   request.UserAgent="LanBridge/1.5"; request.ServicePoint.ConnectionLimit=8;
   if(direct) request.Proxy=null;
   ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
   try {
    using(var response=(HttpWebResponse)request.GetResponse()) using(var stream=response.GetResponseStream()) using(var body=new MemoryStream()) {
     if(response.StatusCode!=HttpStatusCode.OK) throw new IOException("公网入口返回 HTTP "+(int)response.StatusCode);
     byte[] buffer=new byte[1024]; int n;
     while((n=stream.Read(buffer,0,buffer.Length))>0) { if(body.Length+n>8192) throw new IOException("公网入口返回的不是轻桥响应"); body.Write(buffer,0,n); }
     var data=BridgeState.Json().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(body.ToArray()));
     if(Request.Value(data,"app")!="LanBridge" || Request.Value(data,"instance")!=instance || Request.Value(data,"nonce")!=nonce) throw new IOException("公网响应未回到这台电脑");
    }
   } catch(WebException ex) {
    var response=ex.Response as HttpWebResponse;
    if(response!=null) { int status=(int)response.StatusCode; response.Dispose(); throw new IOException("公网入口返回 HTTP "+status+"（临时服务尚未就绪或网络阻止访问）",ex); }
    string reason=ex.Status==WebExceptionStatus.NameResolutionFailure || ex.Status==WebExceptionStatus.ProxyNameResolutionFailure?"公网域名解析失败":ex.Status==WebExceptionStatus.Timeout?"公网 HTTPS 请求超时":ex.Status==WebExceptionStatus.TrustFailure || ex.Status==WebExceptionStatus.SecureChannelFailure?"公网 HTTPS 证书／安全连接失败":ex.Status==WebExceptionStatus.SendFailure || ex.Status==WebExceptionStatus.ReceiveFailure?"网络在公网 HTTPS 连接建立时断开（"+ex.Status+"）":"无法访问公网 HTTPS（"+ex.Status+"）";
    throw new IOException(reason,ex);
   } catch(ArgumentException ex) { throw new IOException("公网入口返回的不是轻桥响应",ex); }
  }
  public void Stop(string message) {
   lock(gate) {
    active=false; state.RemoteRunning=false; generation++; state.CloseRemote();
    if(download!=null) { try { download.CancelAsync(); } catch {} download=null; }
    if(server!=null) { server.Dispose(); server=null; }
    if(child!=null) { try { if(!child.HasExited) child.Kill(); } catch {} child=null; }
    state.RemoteStatus=message; state.Changed();
   }
  }
  public void Dispose() { expiry.Dispose(); Stop("跨网连接已关闭"); }
 }
}
