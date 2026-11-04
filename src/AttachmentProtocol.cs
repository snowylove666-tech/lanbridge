using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace LanBridge {
 public sealed class AttachmentInfo {
  public string group,kind; public int index,count; public long stamp;
  public void Validate() {
   if(!Regex.IsMatch(group??"",@"\A[a-f0-9]{24}\z") || (kind!="image" && kind!="files") || count<1 || count>32 || index<0 || index>=count || (kind=="image" && count!=1) || stamp<=0) throw new ApiError(400,"图片／文件剪贴板元数据无效");
  }
  public AttachmentInfo Copy() { return new AttachmentInfo {group=group,kind=kind,index=index,count=count,stamp=stamp}; }
 }
 public sealed class AttachmentClip {
  public string id,kind,source; public string[] paths; public long stamp;
  [ScriptIgnore] public Func<bool> allow; [ScriptIgnore] public string error;
 }
 public sealed class AttachmentResult { public bool complete,pending,applied; public string status; }
 public sealed partial class BridgeState {
  public Func<AttachmentClip,int> WriteAttachmentClipboard;
  public string AttachmentStatus="等待图片或文件";
  public AttachmentClip LastAttachment;
  readonly System.Collections.Generic.Dictionary<string,AttachmentInfo> pendingClipboardUploads=new System.Collections.Generic.Dictionary<string,AttachmentInfo>();
  readonly long attachmentSessionStart=NowMs();
  public bool AttachmentNewer(AttachmentClip clip) { return clip.id!=PcId && (clip.stamp>DesktopStamp || (clip.stamp==DesktopStamp && String.CompareOrdinal(clip.id,PcId)>0)); }
  public AttachmentClip MarkLocalAttachment() {
   lock(Gate) {
    if(Paused) throw new ApiError(503,"互传已暂停");
    var clip=new AttachmentClip {id=RandomToken(12),stamp=Math.Max(NowMs(),DesktopStamp+1)};
    PcId=clip.id; DesktopStamp=clip.stamp; PcText=ClipText=""; PcOrigin=""; PcTime=ClipTime=DateTime.UtcNow; Revision++;
    return clip;
   }
  }
  // The UI calls this under Gate only after the real clipboard write succeeds.
  public void CommitAttachment(AttachmentClip clip) {
   PcId=clip.id; DesktopStamp=clip.stamp; PcText=ClipText=""; PcOrigin=""; PcTime=ClipTime=DateTime.UtcNow;
   LastAttachment=new AttachmentClip {id=clip.id,kind=clip.kind,source=clip.source,stamp=clip.stamp,paths=(string[])clip.paths.Clone()};
   AttachmentStatus=clip.kind=="image"?"图片已放入剪贴板，可直接粘贴":"已将 "+clip.paths.Length+" 个文件放入剪贴板，可粘贴到支持附件或文件的应用";
   Activity=AttachmentStatus; Revision++; Changed();
  }
  public AttachmentClip PrepareAttachment(string[] paths,string kind,AttachmentClip localEvent=null) {
   if(paths==null || paths.Length==0 || paths.Length>32 || (kind!="image" && kind!="files") || (kind=="image" && paths.Length!=1)) throw new ApiError(400,"一次最多发送 32 个文件；图片剪贴板必须为单张图片");
   foreach(string path in paths) { var file=new FileInfo(path); if(!file.Exists) throw new ApiError(400,"文件不存在；文件夹请先压缩 ZIP"); if(file.Length>MaxFile) throw new ApiError(413,"单个文件最大 1 GB"); }
   var clip=localEvent??MarkLocalAttachment();
   new AttachmentInfo {group=clip.id,kind=kind,index=0,count=paths.Length,stamp=clip.stamp}.Validate();
   return new AttachmentClip {id=clip.id,kind=kind,paths=(string[])paths.Clone(),source=Environment.MachineName,stamp=clip.stamp};
  }
  public void AddClipboardFilesForDevice(string[] paths,string deviceId,string kind,AttachmentClip localEvent=null) {
   var clip=PrepareAttachment(paths,kind,localEvent); var created=new System.Collections.Generic.List<string>();
   try { for(int i=0;i<paths.Length;i++) {
    var info=new FileInfo(paths[i]); var metadata=new AttachmentInfo {group=clip.id,kind=kind,index=i,count=paths.Length,stamp=clip.stamp};
    using(var stream=info.OpenRead()) {
     var file=ReceiveFile(stream,info.Length,info.Name,"Windows",true,delegate { lock(Gate) { if(Paused) throw new ApiError(503,"互传已暂停"); if(!Prefs.devices.Any(x=>x.id==deviceId && x.kind=="computer")) throw new ApiError(401,"电脑已撤销配对"); } },deviceId,null,metadata);
     created.Add(file.id);
    }
   } AttachmentStatus="图片／文件已进入目标电脑的专属队列"; Changed(); }
   catch { foreach(string id in created) try { RemoveFile(id); } catch {} throw; }
  }
  public Transfer ReceiveAttachmentFile(Stream stream,long size,string name,Device device,AttachmentInfo metadata,Action permission) {
   if(device.kind!="computer") throw new ApiError(403,"只有电脑连接可发送剪贴板附件"); metadata.Validate();
   metadata.stamp=Math.Min(metadata.stamp,NowMs()+5000);
   string owner="device:"+device.id,groupKey=owner+"|"+metadata.group+"|",slot=groupKey+metadata.index,key=Hash(slot);
   lock(Gate) {
    CheckDevice(device); var same=Files.Where(x=>x.attachmentOwner==owner && x.attachment!=null && x.attachment.group==metadata.group).ToArray();
    var pending=pendingClipboardUploads.Where(x=>x.Key.StartsWith(groupKey,StringComparison.Ordinal)).Select(x=>x.Value).ToArray();
    if(same.Length>0) metadata.stamp=same[0].attachment.stamp; else if(pending.Length>0) metadata.stamp=pending[0].stamp;
    if(same.Any(x=>x.attachment.kind!=metadata.kind || x.attachment.count!=metadata.count || x.attachment.stamp!=metadata.stamp)) throw new ApiError(409,"剪贴板附件组不一致");
    var duplicate=same.FirstOrDefault(x=>x.attachment.index==metadata.index);
    if(duplicate!=null) throw new ApiError(409,"该剪贴板附件已经保存，请勿重复上传");
    if(pendingClipboardUploads.ContainsKey(slot)) throw new ApiError(409,"该剪贴板附件正在保存，请等待接收结果");
    if(pending.Any(x=>x.kind!=metadata.kind || x.count!=metadata.count || x.stamp!=metadata.stamp)) throw new ApiError(409,"剪贴板附件组不一致");
    pendingClipboardUploads.Add(slot,metadata.Copy());
   }
   try {
    var file=ReceiveFile(stream,size,name,device.name,false,permission,"@host",key,metadata,owner);
    var result=TryApplyAttachment(owner,metadata.group,delegate { lock(Gate) return !Paused && Prefs.devices.Any(x=>x.id==device.id && x.hash==device.hash); });
    AttachmentStatus=result.status; Changed(); return file;
   } finally { lock(Gate) pendingClipboardUploads.Remove(slot); }
  }
  public AttachmentResult TryApplyAttachment(string owner,string group,Func<bool> allow) {
   if(!Regex.IsMatch(group??"",@"\A[a-f0-9]{24}\z")) throw new ApiError(400,"剪贴板附件组无效");
   Transfer[] files; AttachmentClip clip;
   lock(Gate) {
    files=Files.Where(x=>x.attachmentOwner==owner && x.attachment!=null && x.attachment.group==group).ToArray();
    if(files.Length==0) return new AttachmentResult {status="尚未收到附件"};
    var metadata=files[0].attachment; metadata.Validate();
    bool complete=files.Length==metadata.count && files.Select(x=>x.attachment.index).Distinct().Count()==metadata.count && files.All(x=>x.attachment.kind==metadata.kind && x.attachment.count==metadata.count && x.attachment.stamp==metadata.stamp && File.Exists(x.path));
    if(!complete) return new AttachmentResult {status="等待其余剪贴板附件保存完成"};
    if(files.All(x=>x.attachmentHandled)) return new AttachmentResult {complete=true,status="附件已保存；剪贴板已处理或跳过"};
    clip=new AttachmentClip {id=group,kind=metadata.kind,stamp=metadata.stamp,source=files[0].device,paths=files.OrderBy(x=>x.attachment.index).Select(x=>x.path).ToArray(),allow=allow};
    if(Paused || !allow()) return new AttachmentResult {complete=true,pending=true,status="附件已保存，等待连接恢复后处理剪贴板"};
    if(!Prefs.attachmentClipboard || metadata.stamp<attachmentSessionStart || !AttachmentNewer(clip)) {
     string reason=!Prefs.attachmentClipboard?"附件已保存到 Received；未开启图片／文件剪贴板接收":"附件已保存，保留本机更新的剪贴板";
     foreach(var file in files) file.attachmentHandled=true; SaveFiles(); AttachmentStatus=reason; Changed(); return new AttachmentResult {complete=true,status=reason};
    }
   }
   int applied=WriteAttachmentClipboard==null?-1:WriteAttachmentClipboard(clip);
   lock(Gate) {
    if(applied>=0) { foreach(var file in files) file.attachmentHandled=true; SaveFiles(); }
    string message=applied>0?AttachmentStatus:applied<0?"附件已保存，剪贴板暂时被占用，正在重试":clip.error??"附件已保存，保留本机更新的剪贴板";
    AttachmentStatus=message; Changed(); return new AttachmentResult {complete=true,pending=applied<0,applied=applied>0,status=message};
   }
  }
  public void RetryAttachmentGroups(string owner,Func<bool> allow) {
   string[] groups; lock(Gate) groups=Files.Where(x=>x.attachmentOwner==owner && x.attachment!=null && !x.attachmentHandled).GroupBy(x=>x.attachment.group).OrderByDescending(x=>x.Count()==x.First().attachment.count).ThenByDescending(x=>x.First().attachment.stamp).Take(32).Select(x=>x.Key).ToArray();
   foreach(string group in groups) { if(!allow()) return; TryApplyAttachment(owner,group,allow); }
  }
 }
}
