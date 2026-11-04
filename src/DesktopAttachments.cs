using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LanBridge {
 public sealed partial class BridgeForm {
  ClipboardAttachmentSelection ReadClipboardAttachments() {
   if(test) throw new InvalidOperationException("测试模式不读取个人剪贴板");
   return ClipboardAttachmentStorage.Read(Clipboard.GetDataObject(),state.Root);
  }
  void CopyAttachmentsManually(string[] paths,string kind) {
   using(var payload=ClipboardAttachmentStorage.BuildPayload(paths,kind)) {
    if(test) File.WriteAllText(Path.Combine(state.Root,"Data","test-manual-attachment-clipboard.json"),BridgeState.Json().Serialize(new {paths,kind}));
    else { Clipboard.SetDataObject(payload.data,true,5,25); suppressed=GetClipboardSequenceNumber(); capture.Observe(suppressed); }
   }
   state.MarkLocalAttachment();
   Feedback(kind=="image"?"图片已放入剪贴板，可到支持图片的应用粘贴；资源管理器可粘贴为 PNG 文件。":"文件已放入剪贴板，可到资源管理器或支持附件的应用粘贴。");
  }
  int WriteAttachmentClipboard(AttachmentClip clip) {
   int result=-1,canceled=0; var wait=new ManualResetEventSlim(false);
   Action write=delegate {
    try {
     lock(state.Gate) { if(Interlocked.CompareExchange(ref canceled,0,0)!=0 || state.Paused || !state.Prefs.attachmentClipboard || (clip.allow!=null && !clip.allow()) || !state.AttachmentNewer(clip)) {result=0;return;} }
     using(var payload=ClipboardAttachmentStorage.BuildPayload(clip.paths,clip.kind)) {
      lock(state.Gate) {
       if(Interlocked.CompareExchange(ref canceled,0,0)!=0 || state.Paused || !state.Prefs.attachmentClipboard || (clip.allow!=null && !clip.allow()) || !state.AttachmentNewer(clip)) {result=0;return;}
       if(test) File.WriteAllText(Path.Combine(state.Root,"Data","test-received-attachment-clipboard.json"),BridgeState.Json().Serialize(new {clip.id,clip.kind,clip.paths,clip.source,clip.stamp}));
       else { Clipboard.SetDataObject(payload.data,true,5,25); suppressed=GetClipboardSequenceNumber(); capture.Observe(suppressed); }
       state.CommitAttachment(clip); result=1;
      }
     }
    } catch(ExternalException) { result=-1; }
    catch(Exception ex) { clip.error=ex.Message; result=0; }
    finally { try {wait.Set();}catch(ObjectDisposedException){} }
   };
   try { if(InvokeRequired) BeginInvoke(write); else write(); if(!wait.Wait(5000)) Interlocked.Exchange(ref canceled,1); return result; }
   catch {return -1;} finally {wait.Dispose();}
  }
 }
}
