using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace LanBridge {
 public sealed partial class BridgeForm {
  string recentViewKey="";
  void SetAttachmentAuto(bool enabled) {
   lock(state.Gate) { state.Prefs.attachmentClipboard=enabled; state.SaveSettings(); }
   if(!test) capture.Observe(GetClipboardSequenceNumber()); clipTimer.Stop();
   Feedback(enabled?"已开启电脑图片／文件剪贴板同步。之后新复制的内容会发送给已连接电脑。":"图片／文件自动同步已关闭，仍可在文件页粘贴并发送。");
  }
  void PasteClipboardContent() {
   if(test) { Feedback("测试模式不读取个人剪贴板。"); return; }
   if(state.Paused) throw new ApiError(503,"互传已暂停，请先恢复");
   var selection=ReadClipboardAttachments();
   if(selection==null) { Publish(true); return; }
   ShareAttachments(selection,false);
  }
  void PublishAutomatic() {
   if(state.Prefs.attachmentClipboard) {
    var selection=ReadClipboardAttachments(); if(selection!=null) { ShareAttachments(selection,true); return; }
   } else if(Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || Clipboard.ContainsData("PNG")) {
    localClipboardStatus="检测到图片或文件，可在文件页 Ctrl+V 发送，或开启附件自动同步"; return;
   }
   if(state.Prefs.autoClipboard) PublishNow(false);
  }
  void ReleaseStaged(ClipboardAttachmentSelection selection) {
   if(!selection.staged) return; string folder=Path.Combine(state.Root,"Data","ClipboardStaging");
   foreach(string path in selection.paths) if(BridgeState.Inside(path,folder) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),@"\Aclip-[0-9]{8}-[0-9]{6}-[a-f0-9]{32}\.png\z")) try { File.Delete(path); } catch {}
  }
  void ShareAttachments(ClipboardAttachmentSelection selection,bool automatic) {
   var clip=state.MarkLocalAttachment();
   var targets=new System.Collections.Generic.List<FileTarget>();
   if(automatic) {
    if(state.Peer!=null && state.Peer.Connected) targets.Add(new FileTarget {outgoing=true,id="@connected",name=state.Peer.Name});
    lock(state.Gate) foreach(var device in state.Prefs.devices.Where(x=>x.kind=="computer" && DateTime.UtcNow-x.seen<TimeSpan.FromSeconds(12))) targets.Add(new FileTarget {id=device.id,name=device.name});
   } else { var selected=fileTargets.SelectedItem as FileTarget; if(selected!=null) targets.Add(selected); }
   if(targets.Count==0) { ReleaseStaged(selection); localClipboardStatus="尚未连接电脑，图片／文件未自动发送；连接后重新复制即可"; if(!automatic) throw new ApiError(409,"请先选择收件设备"); return; }
   int remaining=targets.Count,started=0,failed=0; Action done=delegate { if(Interlocked.Decrement(ref remaining)==0) ReleaseStaged(selection); };
   foreach(var target in targets) {
    try {
     if(target.id=="@shared") { QueueFiles(selection.paths,null,done); started++; continue; }
     if(target.outgoing) state.Peer.SendClipboardFiles(selection.paths,selection.kind,clip,done);
     else { string recipient=target.id; ThreadPool.QueueUserWorkItem(delegate { try { state.AddClipboardFilesForDevice(selection.paths,recipient,selection.kind,clip); OnUi(delegate { Feedback("附件已放入所选电脑的专属队列，等待接收确认。"); }); } catch(Exception ex) { string message=ex.Message; OnUi(delegate { Feedback("图片／文件发送失败："+message,true); }); } finally { done(); } }); }
     started++;
    } catch(Exception ex) { failed++; done(); Feedback("图片／文件未发送："+ex.Message,true); }
   }
   localClipboardStatus=started==0?"图片／文件未开始发送，请查看失败原因":(selection.kind=="image"?"图片":selection.paths.Length+" 个文件")+"已开始发送，接收结果见文件页";
   if(!automatic && failed==0) Feedback(localClipboardStatus+"。接收方开启附件同步后可直接粘贴，否则先保存到 Received。");
  }
  void CopySelectedAttachments() {
   var selected=files.SelectedItems.Cast<ListViewItem>().Select(x=>(Transfer)x.Tag).ToArray();
   if(selected.Length==0) throw new ApiError(400,"请先选择已保存的文件记录");
   string kind=selected.Length==1 && selected[0].attachment!=null && selected[0].attachment.kind=="image"?"image":"files";
   CopyAttachmentsManually(selected.Select(x=>x.path).ToArray(),kind);
  }
  void RefreshRecentConnections() {
   var recent=state.GetRecentPeers(); string key=String.Join("|",recent.Select(x=>x.id+":"+x.name+":"+x.lastUsed.Ticks).ToArray());
   recentArea.Visible=recent.Length>0; connectionLayout.RowStyles[1].Height=recent.Length>0?76:0;
   if(key==recentViewKey) return; recentViewKey=key;
   foreach(Control control in recentButtons.Controls.Cast<Control>().ToArray())control.Dispose(); recentButtons.Controls.Clear();
   for(int i=0;i<Math.Min(3,recent.Length);i++) { var item=recent[i]; string id=item.id; var button=Button(item.name+"  ·  再次连接",delegate { ReconnectHistory(id); }); button.AutoSize=false; button.AutoEllipsis=true; button.Dock=DockStyle.Fill; button.MinimumSize=System.Drawing.Size.Empty; button.AccessibleDescription=item.remote?"已保存的临时互联网地址；通道关闭后需要新邀请":"已保存的电脑连接，无需重新查找"; recentButtons.Controls.Add(button,i,0); }
   historyDevices.Items.Clear(); foreach(var item in recent) historyDevices.Items.Add(new ListViewItem(new[]{item.name,item.remote?"临时互联网地址":"直连地址",item.lastUsed==DateTime.MinValue?"旧版上次连接":item.lastUsed.ToLocalTime().ToString("MM-dd HH:mm"),item.url}) {Name=item.id});
  }
  void ReconnectHistory(string id) {
   state.ReconnectPeer(id); SelectPage(0); SelectConnectionMode(0);
   var entry=state.GetRecentPeers().FirstOrDefault(x=>x.id==id);
   Feedback(entry!=null && entry.remote?"正在重连。临时入口若已关闭或换地址，需要对方提供新邀请。":"正在使用已保存的授权重新连接，无需再次输入地址。");
  }
  void ReconnectSelectedHistory() { if(historyDevices.SelectedItems.Count==0) throw new ApiError(400,"请先选择历史电脑"); ReconnectHistory(historyDevices.SelectedItems[0].Name); }
  void OpenBluetoothTransfer() {
   if(test) {Feedback("测试模式不打开蓝牙或更改系统设置。");return;}
   string program=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"fsquirt.exe"); if(!File.Exists(program)) throw new ApiError(404,"当前 Windows 未提供蓝牙文件传输程序，请检查蓝牙硬件与系统支持");
   System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program) {UseShellExecute=true});
   Feedback("已打开 Windows 蓝牙文件传输。两台 Windows 先在系统配对；iPhone 不能使用这个通用蓝牙文件通道。");
  }
  protected override bool ProcessCmdKey(ref Message message,Keys keys) {
   if(keys==(Keys.Control|Keys.V) && pages!=null && pages[2].Visible && !(ActiveControl is TextBoxBase)) { Run(PasteClipboardContent); return true; }
   return base.ProcessCmdKey(ref message,keys);
  }
 }
}
