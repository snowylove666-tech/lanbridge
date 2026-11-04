using System;
using System.Runtime.InteropServices;

namespace LanBridge {
 // Scheduling policy is separate from the Windows clipboard so busy/replacement
 // cases can be tested without touching the user's real clipboard.
 public sealed class ClipboardCapture {
  uint observed,pending; int failures;
  public bool NeedsCapture(uint sequence,uint suppressed) { return sequence!=observed && sequence!=suppressed; }
  public void Observe(uint sequence) { observed=sequence; failures=0; }
  public void Begin(uint sequence) { pending=sequence; failures=0; }
  public CaptureResult Try(uint sequence,uint suppressed,Action publish) {
   if(!NeedsCapture(sequence,suppressed)) return new CaptureResult();
   if(sequence!=pending) { Begin(sequence); return new CaptureResult {retryAfter=140}; }
   try { publish(); Observe(sequence); return new CaptureResult {published=true}; }
   catch(ExternalException) {
    if(++failures<6) return new CaptureResult {retryAfter=Math.Min(900,100*failures),message="剪贴板暂时被占用，正在重试…"};
    Observe(sequence); return new CaptureResult {failed=true,message="读取失败，请重新复制，或点击“共享当前文字”重试"};
   } catch(Exception ex) { Observe(sequence); return new CaptureResult {failed=true,message="共享失败："+ex.Message}; }
  }
 }
 public sealed class CaptureResult { public int retryAfter; public bool published,failed; public string message; }
}
