using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LanBridge {
 public sealed partial class BridgeForm : Form {
  [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
  [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
  [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd,int id);
  readonly BridgeState state; readonly bool test;
  NotifyIcon tray; System.Windows.Forms.Timer timer,clipTimer;
  bool closing,refreshing,scanning,fileTargetChosen; uint suppressed;
  readonly ClipboardCapture capture=new ClipboardCapture(); DateTime nextAddressCheck;
  string filesViewKey="",lastActivity="",lastPeerStatus="",lastFileStatus="",localClipboardStatus="等待新复制的文字";
  string localQrValue="",remoteQrValue=""; DateTime feedbackUntil;
  string copiedInviteSecret="";
  readonly Queue<string> history=new Queue<string>();
  sealed class FileTarget { public string id,name; public bool outgoing; public override string ToString() { return name; } }
  public BridgeForm(BridgeState state,bool test) {
   this.state=state; this.test=test;
   SuspendLayout(); AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
   Text="轻桥 1.5 · 设备互传"; Font=new Font("Microsoft YaHei UI",10); BackColor=Color.FromArgb(245,247,251);
   ClientSize=new Size(1040,740); MinimumSize=new Size(900,640); StartPosition=FormStartPosition.CenterScreen;
   Build(); ResumeLayout(true);
   state.WriteDesktopClipboard=WriteDesktop;
   state.WriteAttachmentClipboard=WriteAttachmentClipboard;
   ClipboardAttachmentStorage.TrimStaging(state.Root);
   state.Changed=delegate { if(IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)RefreshView); } catch {} };
   timer=new System.Windows.Forms.Timer {Interval=1200}; timer.Tick+=delegate { WatchNetwork(); RefreshView(); WatchClipboard(); }; timer.Start();
   clipTimer=new System.Windows.Forms.Timer {Interval=140}; clipTimer.Tick+=delegate { clipTimer.Stop(); CaptureAutomatic(); };
   if(!test) SetupTray();
   Shown+=delegate { var area=Screen.FromControl(this).WorkingArea; if(Height>area.Height-20) Height=area.Height-20; RefreshAddresses(); RefreshView(); };
   FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(!closing && !test && e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Hide(); tray.ShowBalloonTip(1800,"轻桥仍在运行","双击托盘图标打开；右键可完全退出。",ToolTipIcon.Info); } };
   FormClosed+=delegate { if(tray!=null) tray.Dispose(); timer.Dispose(); clipTimer.Dispose(); if(localQr.Image!=null) localQr.Image.Dispose(); if(remoteQr.Image!=null) remoteQr.Image.Dispose(); };
   AllowDrop=true; DragEnter+=delegate(object s,DragEventArgs e) { e.Effect=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None; };
   DragDrop+=delegate(object s,DragEventArgs e) { var paths=e.Data.GetData(DataFormats.FileDrop) as string[]; if(paths!=null) { SelectPage(2); Run(delegate { SendSelectedFiles(paths); }); } };
  }
  void Run(Action action) { try { action(); } catch(Exception ex) { Feedback(ex.Message,true); } }
  void OnUi(Action action) { if(IsDisposed) return; try { BeginInvoke((Action)delegate { if(!IsDisposed) action(); }); } catch {} }
  void Record(string message) {
   if(String.IsNullOrEmpty(message)) return;
   history.Enqueue(DateTime.Now.ToString("HH:mm:ss")+"  "+message.Replace("\r"," ").Replace("\n"," ")); while(history.Count>60) history.Dequeue();
   if(activityLog!=null) activityLog.Text=String.Join(Environment.NewLine,history.Reverse().ToArray());
  }
  void Feedback(string message,bool error=false) {
   notice.Text=message; notice.ForeColor=error?Color.FromArgb(160,52,38):Color.FromArgb(30,91,86); feedbackUntil=DateTime.UtcNow.AddSeconds(error?30:10); Record(message);
  }
  void CopyText(string text,string confirmation) {
   if(test) { Feedback(confirmation+"（测试模式未改写系统剪贴板）"); return; }
   Clipboard.SetText(text); suppressed=GetClipboardSequenceNumber(); capture.Observe(suppressed); Feedback(confirmation);
  }
  void RefreshAddresses() {
   string old=addresses.SelectedItem as string; var values=BridgeState.Addresses(); addresses.Items.Clear(); foreach(var value in values) addresses.Items.Add(value);
   if(values.Count>0) addresses.SelectedItem=values.Contains(old)?old:values[0];
   localAddress.Text="http://"+state.LocalHost+":"+state.Port+"/";
  }
  void WatchNetwork() {
   if(DateTime.UtcNow<nextAddressCheck) return; nextAddressCheck=DateTime.UtcNow.AddSeconds(10);
   var current=BridgeState.Addresses(); if(!addresses.Items.Cast<string>().SequenceEqual(current)) { RefreshAddresses(); Record("网络地址已更新，备用 IP 和二维码已刷新"); }
  }
  void RefreshQr(PictureBox box,string value,ref string previous) {
   if(DateTime.UtcNow>=state.PairUntil) value="";
   if(value==previous) return; previous=value; var old=box.Image; box.Image=value==""?null:PairQr.Render(value); if(old!=null) old.Dispose();
  }
  void RefreshView() {
   if(IsDisposed) return; refreshing=true;
   try { lock(state.Gate) {
    state.ExpireClipboard(); auto.Checked=state.Prefs.autoClipboard; pause.Checked=state.Paused; autoAttachments.Checked=state.Prefs.attachmentClipboard;
    attachmentStatus.Text=state.AttachmentStatus;
    var pending=state.Joins.Find(x=>x.until>DateTime.UtcNow && x.token==null && !x.rejected);
    if(pending!=pendingJoin) { pendingJoin=pending; approveSync.Checked=pending!=null && pending.kind=="computer"; }
    joinBanner.Visible=pending!=null; shell.RowStyles[1].Height=pending==null?0:92;
    if(pending!=null) { joinLabel.Text=pending.name+" 请求连接 · 请核对识别号 "+pending.check; approveSync.Visible=pending.kind=="computer"; }
    pairCode.Text=DateTime.UtcNow<state.PairUntil ? "备用配对码  "+state.PairCode+"   ·   剩余 "+Math.Max(0,(int)(state.PairUntil-DateTime.UtcNow).TotalSeconds)+" 秒" : "配对码已使用或过期，点击刷新即可添加设备";
    string ip=addresses.SelectedItem as string;
    RefreshQr(localQr,ip==null?"":"http://"+ip+":"+state.Port+"/#pair="+state.PairSecret,ref localQrValue);
    RefreshQr(remoteQr,state.RemoteOrigin==""?"":state.RemoteOrigin+"/#pair="+state.PairSecret,ref remoteQrValue);
    remoteStatus.Text=state.RemoteStatus+(state.RemoteOrigin!=""?"\n剩余 "+Math.Max(0,(int)(state.RemoteUntil-DateTime.UtcNow).TotalMinutes)+" 分钟":"");
    remoteToggle.Text=state.RemoteRunning || state.RemoteOrigin!=""?"关闭互联网入口":"准备互联网入口";
    inviteCopyButton.Enabled=!state.Paused;
    if(copiedInviteSecret!="" && (state.PairSecret!=copiedInviteSecret || DateTime.UtcNow>=state.PairUntil)) { remoteInvitation.Text="上次邀请已使用、已刷新或过期。需要添加新设备时，再点击复制连接邀请。"; copiedInviteSecret=""; }
    var temporary=state.Prefs.devices.Where(x=>x.temporary).ToArray();
    if(state.RemoteOrigin!="" && temporary.Length>0) remoteInviteHint.Text="通过临时入口已配对："+String.Join("、",temporary.Select(x=>x.name).ToArray())+"。同步状态见“剪贴板”，文件到“文件”页发送。";
    else if(state.RemoteOrigin!="") remoteInviteHint.Text=state.RemoteVerified?"本机公网检测已通过，等待对方粘贴邀请加入。电脑不扫码。":"临时入口已创建，等待对方尝试；是否连通以设备连接状态为准。";
    else remoteInviteHint.Text="可直接复制邀请给另一台电脑；若直连不成功，再准备互联网入口并更新邀请。";
    bool connected=state.Peer!=null && state.Peer.Connected;
    headerStatus.Text=state.Paused?"互传已暂停":connected?"已连接 "+state.Peer.Name:state.Prefs.devices.Any(x=>DateTime.UtcNow-x.seen<TimeSpan.FromSeconds(12))?"设备近期有响应":"等待设备连接";
    headerStatus.ForeColor=state.Paused?Color.FromArgb(160,92,30):Color.FromArgb(51,73,94);
    if(state.Activity!=lastActivity) { lastActivity=state.Activity; Record(lastActivity); }
    if(DateTime.UtcNow>=feedbackUntil) { notice.ForeColor=Color.FromArgb(87,102,120); notice.Text=state.Paused?"已暂停。到设置中恢复后才能继续同步和传输。":state.Activity; }
    RefreshDevices(connected); RefreshTargets(connected); RefreshFileList();
    RefreshRecentConnections();
    peerStatus.Text=state.Peer==null?"正在启动连接服务":state.Peer.Status;
    remoteJoinStatus.Text=state.Peer==null?"正在启动连接服务":state.Peer.Status;
    if(state.Peer!=null && lastPeerStatus!=state.Peer.Status) { lastPeerStatus=state.Peer.Status; Record(lastPeerStatus); }
    if(state.Peer!=null && lastFileStatus!=state.Peer.FileStatus) { lastFileStatus=state.Peer.FileStatus; if(lastFileStatus!="") Record(lastFileStatus); }
    fileStatus.Text=state.Peer!=null && state.Peer.FileStatus!=""?state.Peer.FileStatus:"选择收件设备，再发送或拖入文件。收到的文件保存在 Received。";
    clipboardLocal.Text=state.Paused?"已暂停全部互传":!state.Prefs.autoClipboard?"电脑文字自动同步已关闭。仍可手动共享或通过手机快捷指令收发。":"已开启自动同步 · "+localClipboardStatus;
    clipboardPeer.Text=state.Peer==null?"":state.Peer.ClipboardStatus;
    string text=state.ClipText.Length>0?state.ClipSource+" · "+state.ClipTime.ToLocalTime().ToString("HH:mm:ss")+Environment.NewLine+state.ClipText:"暂无共享文字。复制后会显示在这里；这里有文字不代表对方已经粘贴。";
    if(preview.Text!=text) preview.Text=text;
    RefreshRemoteFiles();
   } } finally { refreshing=false; }
  }
  void RefreshDevices(bool connected) {
   string selected=devices.SelectedItems.Count>0?devices.SelectedItems[0].Name:"";
   devices.BeginUpdate(); devices.Items.Clear();
   if(state.Prefs.peer!=null && state.Prefs.peer.enabled) { var p=state.Prefs.peer; var item=new ListViewItem(new[]{p.name??"另一台电脑","电脑",connected?"连接正常":"正在连接／等待重试","本机发起"}) {Name="@outgoing"}; devices.Items.Add(item); item.Selected=selected==item.Name; }
   foreach(var d in state.Prefs.devices) { bool recent=DateTime.UtcNow-d.seen<TimeSpan.FromSeconds(12); var item=new ListViewItem(new[]{d.name,d.kind=="computer"?"电脑":"手机／浏览器",recent?"最近有响应":"已授权，当前未活跃",d.seen.ToLocalTime().ToString("MM-dd HH:mm:ss")}) {Name=d.id}; devices.Items.Add(item); item.Selected=item.Name==selected; }
   devices.EndUpdate(); deviceHint.Text=devices.Items.Count==0?"首次连接请选择下方方式；已配对设备统一在设置中管理。":"已保存 "+devices.Items.Count+" 个连接／授权 · 查看和移除请到设置。";
  }
  void RefreshTargets(bool connected) {
   var targets=new List<FileTarget>();
   if(connected) targets.Add(new FileTarget {id="@connected",outgoing=true,name=state.Peer.Name+" · 当前连接的电脑"});
   foreach(var d in state.Prefs.devices.Where(x=>x.kind=="computer")) targets.Add(new FileTarget {id=d.id,name=d.name+(DateTime.UtcNow-d.seen<TimeSpan.FromSeconds(12)?" · 近期有响应":" · 等待上线后接收")});
   targets.Add(new FileTarget {id="@shared",name="手机／浏览器共享区 · 已配对设备可下载"});
   string selected=fileTargetChosen && fileTargets.SelectedItem is FileTarget?((FileTarget)fileTargets.SelectedItem).id:"";
   if(!fileTargets.Items.Cast<FileTarget>().Select(x=>x.id+"|"+x.name).SequenceEqual(targets.Select(x=>x.id+"|"+x.name))) {
    fileTargets.Items.Clear(); foreach(var t in targets) fileTargets.Items.Add(t);
    int selectedIndex=targets.FindIndex(x=>x.id==selected); if(selectedIndex<0) fileTargetChosen=false;
    fileTargets.SelectedIndex=Math.Max(0,selectedIndex);
   }
   var target=fileTargets.SelectedItem as FileTarget;
   fileTargetHint.Text=target==null?"请先选择收件位置":target.id=="@shared"?"文件放入共享区后，手机需打开网页下载；放入成功不等于手机已经保存。":target.outgoing?"直接发送给所选电脑，对方自动保存到 Received。":"文件先进入此电脑的专属发送队列；收到确认后才标为“对方已接收”。";
  }
  void RefreshFileList() {
   string key=String.Join("|",state.Files.OrderByDescending(x=>x.time).Select(x=>x.id+":"+x.delivered).ToArray()); if(filesViewKey==key) return;
   string selected=files.SelectedItems.Count>0?files.SelectedItems[0].Name:"";
   files.BeginUpdate(); files.Items.Clear();
   foreach(var f in state.Files.OrderByDescending(x=>x.time)) {
    string result=f.direction=="toPhone"?(String.IsNullOrEmpty(f.recipient)?"可供下载 · 未确认保存":f.delivered?"对方已接收":"等待对方接收"):"已保存到本机";
    string target=f.direction=="toPhone"?(String.IsNullOrEmpty(f.recipient)?"手机／浏览器共享区":state.Prefs.devices.Where(x=>x.id==f.recipient).Select(x=>x.name).FirstOrDefault()??"已移除的设备"):f.device;
    var item=new ListViewItem(new[]{f.name,result,target,SizeText(f.size),f.time.ToLocalTime().ToString("MM-dd HH:mm")}) {Name=f.id,Tag=f}; files.Items.Add(item); item.Selected=item.Name==selected;
   } files.EndUpdate(); filesViewKey=key;
  }
  void RefreshRemoteFiles() {
   if(state.Peer==null) return; var values=state.Peer.Files.Where(x=>!x.targeted).ToArray();
   if(peerFiles.Items.Cast<ListViewItem>().Select(x=>x.Name).SequenceEqual(values.Select(x=>x.id))) return;
   peerFiles.Items.Clear(); foreach(var file in values) peerFiles.Items.Add(new ListViewItem(new[]{file.name,SizeText(file.size)}) {Name=file.id,Tag=file});
  }
  void Approve() {
   if(pendingJoin==null) return;
   bool enable=pendingJoin.kind=="computer" && approveSync.Checked;
   state.ApproveJoin(pendingJoin.key,true); if(enable) SetAuto(true); Feedback("已允许设备连接"+(enable?"，本机新复制的文字将自动共享":"")); RefreshView();
  }
  void SetAuto(bool enabled) {
   lock(state.Gate) { state.Prefs.autoClipboard=enabled; state.SaveSettings(); }
   if(state.Peer!=null) state.Peer.SetClipboardEnabled(enabled);
   if(!test) capture.Observe(GetClipboardSequenceNumber()); clipTimer.Stop();
   localClipboardStatus=enabled?"等待新复制的文字":"自动共享已关闭"; RefreshView();
  }
  void ScanComputers() {
   if(scanning) return; scanning=true; scanButton.Enabled=false; scanStatus.Text="正在查找电脑…";
   ThreadPool.QueueUserWorkItem(delegate { try { var found=ComputerDiscovery.Scan(state.Prefs.installId); OnUi(delegate { nearby.Items.Clear(); foreach(var item in found) nearby.Items.Add(item); scanStatus.Text=found.Length==0?"没有发现电脑。请使用上方“创建／加入邀请”；两台电脑都需运行轻桥。":"找到 "+found.Length+" 台电脑，请选择后连接。"; }); } catch(Exception ex) { string message=ex.Message; OnUi(delegate { scanStatus.Text="查找失败："+message+"。也可使用连接邀请。"; }); } finally { OnUi(delegate { scanning=false; scanButton.Enabled=true; }); } });
  }
  void ConnectComputer() {
   if(state.Peer==null) throw new ApiError(409,"连接服务正在启动");
   if(DeviceInvitation.IsBundle(peerAddress.Text)) {state.Peer.Connect(peerAddress.Text,connectClipboard.Checked);if(connectClipboard.Checked && !state.Prefs.autoClipboard)SetAuto(true);Feedback("正在自动尝试邀请中的连接方式，无需判断网络类型。");return;}
   if(peerAddress.Text.Trim().StartsWith("https://",StringComparison.OrdinalIgnoreCase)) { remoteAddress.Text=peerAddress.Text; SelectConnectionMode(1); SelectRemoteStep(1); JoinRemoteInvitation(); return; }
   string address=PeerClient.ValidateAddress(peerAddress.Text);
   state.Peer.Connect(address,connectClipboard.Checked);
   if(connectClipboard.Checked && !state.Prefs.autoClipboard) SetAuto(true);
   Feedback("正在连接同网电脑。请在对方电脑核对识别号并允许。");
  }
  void CreateRemoteInvitation() {
   if(state.Paused) throw new ApiError(503,"互传已暂停，请先在设置中恢复。");
   if(inviteShareClipboard.Checked && !state.Prefs.autoClipboard) SetAuto(true);
   if(DateTime.UtcNow>=state.PairUntil || String.IsNullOrEmpty(state.PairSecret)) state.RefreshPairing();
   copiedInviteSecret=state.PairSecret;
   string invite=DeviceInvitation.Create(state); remoteInvitation.Text=invite;
   CopyText(invite,state.RemoteOrigin!=""?"邀请已复制，含直连与互联网入口。另一台电脑粘贴后自动尝试。":"直连邀请已复制，另一台电脑粘贴即可尝试。如无法互通，可准备互联网入口后更新邀请。"); RefreshView();
  }
  void PasteRemoteInvitation() {
   if(test) { Feedback("测试模式不读取个人剪贴板。可在输入框中填写生成的测试邀请。"); return; }
   if(!Clipboard.ContainsText()) throw new ApiError(400,"剪贴板里没有邀请。请先复制另一台电脑发来的完整邀请。");
   string raw=Clipboard.GetText(); string invitation=DeviceInvitation.IsBundle(raw)?raw:ConnectionInvitation.Parse(raw); if(DeviceInvitation.IsBundle(raw))DeviceInvitation.Parse(raw); remoteAddress.Text=invitation; JoinRemoteInvitation();
  }
  void JoinRemoteInvitation() {
   string invitation=DeviceInvitation.IsBundle(remoteAddress.Text)?remoteAddress.Text:ConnectionInvitation.Parse(remoteAddress.Text); if(DeviceInvitation.IsBundle(invitation))DeviceInvitation.Parse(invitation);
   if(state.Peer==null) throw new ApiError(409,"连接服务正在启动");
   state.Peer.Connect(invitation,remoteConnectClipboard.Checked);
   if(remoteConnectClipboard.Checked && !state.Prefs.autoClipboard) SetAuto(true);
   Feedback("正在加入对方电脑，程序会尝试可用入口。不需要扫码或在本机另开通道。");
  }
  void DisconnectSelected() {
   if(devices.SelectedItems.Count==0) throw new ApiError(400,"请先选中一台设备");
   string id=devices.SelectedItems[0].Name;
   if(id=="@outgoing") state.Peer.Disconnect();
   else lock(state.Gate) { state.Prefs.devices.RemoveAll(x=>x.id==id); state.SaveSettings(); }
   Feedback("已断开选中的连接；其他设备不受影响。"); RefreshView();
  }
  void CheckConnection() {
   RefreshAddresses(); int count=addresses.Items.Count;
   string report="本机程序正在运行。"+(count==0?"未找到可用的局域网 IPv4 地址，请先连接 Wi-Fi／热点。":"检测到 "+count+" 个局域网地址。")+Environment.NewLine;
   report+=state.Peer!=null && state.Peer.Connected?"另一台电脑的接口已有响应。若内容没收到，请到“剪贴板”页查看同步状态。":"尚未确认对端可达。两台都运行轻桥后，先查找；找不到时使用对方创建的连接邀请。";
   report+=Environment.NewLine+"本机检查无法证明防火墙或热点允许设备互访。若 IP 地址仍不通，确认同一网络、轻桥已获专用网络放行，且不是隔离的访客网络。";
   if(state.RemoteDiagnostic!="") report+=Environment.NewLine+"互联网诊断："+state.RemoteDiagnostic;
   diagnostics.Text=report; Record("已刷新地址并检查本机连接状态");
  }
  void SendSelectedFiles(string[] paths) {
   if(state.Paused) throw new ApiError(503,"互传已暂停，请先恢复");
   var target=fileTargets.SelectedItem as FileTarget; if(target==null) throw new ApiError(400,"请先选择收件设备");
   if(target.outgoing) { state.Peer.SendFiles(paths); Feedback("已开始向 "+state.Peer.Name+" 发送，请查看下方传输结果。"); return; }
   QueueFiles(paths,target.id=="@shared"?null:target.id);
  }
  void QueueFiles(string[] paths,string recipient=null,Action finished=null) {
   Feedback("正在准备 "+paths.Length+" 个文件…");
   ThreadPool.QueueUserWorkItem(delegate { int success=0,failed=0; var errors=new List<string>(); foreach(string path in paths) {
    try { if(!File.Exists(path)) throw new IOException("文件不存在，或为文件夹；文件夹请先压缩为 ZIP"); if(recipient==null) state.AddFile(path); else state.AddFileForDevice(path,recipient); success++; }
    catch(Exception ex) { failed++; errors.Add(Path.GetFileName(path)+"："+ex.Message); }
   } if(finished!=null) finished(); string message=success+" 个文件"+(recipient==null?"已放入共享区，等待手机／浏览器下载。":"已排队，等待目标电脑接收。")+(failed>0?" "+failed+" 个失败："+String.Join("；",errors.ToArray()):""); OnUi(delegate { Feedback(message,failed>0); RefreshView(); }); });
  }
  void ChooseFiles() { using(var picker=new OpenFileDialog {Multiselect=true,Title="发送文件给当前收件设备"}) if(picker.ShowDialog(this)==DialogResult.OK) SendSelectedFiles(picker.FileNames); }
  public void AcceptCommand(string[] paths) { OnUi(delegate { if(paths.Length==0) ShowMain(); else QueueFiles(paths); }); }
  void RemoveSelectedFile() { if(files.SelectedItems.Count==0) throw new ApiError(400,"先选中要移除的记录"); state.RemoveFile(((Transfer)files.SelectedItems[0].Tag).id); Feedback("记录已移除；接收到本机的原文件仍保留。"); }
  void WatchClipboard() {
   if(test || (!state.Prefs.autoClipboard && !state.Prefs.attachmentClipboard) || state.Paused || clipTimer.Enabled) return;
   uint sequence=GetClipboardSequenceNumber(); if(capture.NeedsCapture(sequence,suppressed)) StartCapture(sequence);
  }
  void StartCapture(uint sequence) { capture.Begin(sequence); clipTimer.Stop(); clipTimer.Interval=140; clipTimer.Start(); }
  void CaptureAutomatic() {
   if(test || (!state.Prefs.autoClipboard && !state.Prefs.attachmentClipboard) || state.Paused) return;
   var result=capture.Try(GetClipboardSequenceNumber(),suppressed,PublishAutomatic);
   if(result.message!=null) localClipboardStatus=result.message;
   if(result.retryAfter>0) { clipTimer.Interval=result.retryAfter; clipTimer.Start(); }
   if(result.failed) Feedback(result.message,true);
  }
  void Publish(bool report) { Run(delegate { PublishNow(report); if(!test) capture.Observe(GetClipboardSequenceNumber()); }); }
  void PublishNow(bool report) {
   if(test) { if(report) Feedback("测试模式不读取个人剪贴板"); return; }
   if(Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) { localClipboardStatus="已跳过来源应用禁止共享的内容"; if(report) throw new ApiError(400,localClipboardStatus); return; }
   if(!Clipboard.ContainsText()) { if(report) throw new ApiError(400,"剪贴板里没有文字。图片和文件请到“文件”页发送。"); localClipboardStatus="已跳过图片／文件，等待文字"; return; }
   state.PublishClipboard(Clipboard.GetText(),"Windows",false); localClipboardStatus="已读取 "+DateTime.Now.ToString("HH:mm:ss")+" 的复制内容";
   if(report) Feedback(state.Prefs.autoClipboard?"文字已进入共享区。电脑接收结果见同步状态；iPhone 需运行背面快捷指令。":"文字已进入手机／浏览器共享区。电脑之间传文字请开启上方自动同步。");
  }
  int WriteDesktop(DesktopClip clip) {
   int result=-1,canceled=0; var wait=new ManualResetEventSlim(false);
   Action write=delegate { try { lock(state.Gate) {
    if(Interlocked.CompareExchange(ref canceled,0,0)!=0 || state.Paused || (clip.allow!=null && !clip.allow()) || !state.DesktopNewer(clip)) { result=0; return; }
    if(test) File.WriteAllText(Path.Combine(state.Root,"Data","test-received-clipboard.txt"),clip.text);
    else { bool copied=false; for(int i=0;i<5;i++) try { Clipboard.SetText(clip.text); suppressed=GetClipboardSequenceNumber(); capture.Observe(suppressed); copied=true; break; } catch(ExternalException) { Thread.Sleep(25); } if(!copied) return; }
    state.CommitDesktop(clip); result=1;
   } } finally { try { wait.Set(); } catch(ObjectDisposedException) {} } };
   try { if(InvokeRequired) BeginInvoke(write); else write(); if(!wait.Wait(3000)) Interlocked.Exchange(ref canceled,1); return result; } catch { return -1; } finally { wait.Dispose(); }
  }
  static string SizeText(long n) { return n<1024?n+" B":n<1048576?(n/1024.0).ToString("0.0")+" KB":(n/1048576.0).ToString("0.0")+" MB"; }
  void ShowMain() { Show(); WindowState=FormWindowState.Normal; Activate(); }
  void SetupTray() {
   var menu=new ContextMenuStrip(); menu.Items.Add("打开轻桥",null,delegate { ShowMain(); }); menu.Items.Add("共享当前文字  Alt+Shift+C",null,delegate { Publish(true); }); menu.Items.Add("发送文件…",null,delegate { ShowMain(); SelectPage(2); Run(ChooseFiles); }); menu.Items.Add("打开接收文件夹",null,delegate { Run(delegate { Process.Start("explorer.exe",state.Received); }); }); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出轻桥",null,delegate { closing=true; Close(); Application.Exit(); });
   Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath); tray=new NotifyIcon {Icon=Icon,Text="轻桥 · 设备互传",Visible=true,ContextMenuStrip=menu}; tray.DoubleClick+=delegate { ShowMain(); };
  }
  protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if(!test) { capture.Observe(GetClipboardSequenceNumber()); AddClipboardFormatListener(Handle); RegisterHotKey(Handle,1,0x4005,0x43); RegisterHotKey(Handle,2,0x4005,0x4f); } }
  protected override void OnHandleDestroyed(EventArgs e) { if(!test) { RemoveClipboardFormatListener(Handle); UnregisterHotKey(Handle,1); UnregisterHotKey(Handle,2); } base.OnHandleDestroyed(e); }
  protected override void WndProc(ref Message m) { if(m.Msg==0x031d && clipTimer!=null && (state.Prefs.autoClipboard || state.Prefs.attachmentClipboard) && !state.Paused && !test) StartCapture(GetClipboardSequenceNumber()); if(m.Msg==0x0312) { if(m.WParam.ToInt32()==1) Publish(true); if(m.WParam.ToInt32()==2) ShowMain(); } base.WndProc(ref m); }
 }
}
