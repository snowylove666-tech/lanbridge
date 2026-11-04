using System;
using System.Runtime.InteropServices;
using LanBridge;

static class ClipboardCaptureTest {
 static int checks;
 static void Assert(bool value,string name) { if(!value) throw new Exception(name); checks++; Console.WriteLine("PASS "+name); }
 static void Main() {
  var reader=new ClipboardCapture(); reader.Observe(10); reader.Begin(11); int reads=0;
  Action busyOnce=delegate { if(++reads==1) throw new ExternalException("busy"); };
  var result=reader.Try(11,0,busyOnce); Assert(result.retryAfter>0 && !result.failed,"Busy clipboard schedules a retry instead of silently dropping the copy");
  result=reader.Try(11,0,busyOnce); Assert(result.published && reads==2,"A later attempt publishes the same pending copy");
  reader.Try(11,0,delegate { reads++; }); Assert(reads==2,"Successful copy is not published twice");
  reader.Begin(12); reader.Try(12,0,delegate { throw new ExternalException(); });
  result=reader.Try(13,0,delegate { throw new Exception("must not read old copy"); }); Assert(result.retryAfter==140,"A newer copy replaces a busy older copy and is debounced");
  result=reader.Try(13,0,delegate { reads++; }); Assert(result.published,"Newest replacement is eventually published");
  reader.Begin(14); for(int i=0;i<6;i++) result=reader.Try(14,0,delegate { throw new ExternalException(); });
  Assert(result.failed && result.retryAfter==0 && result.message.Length>0,"Persistent busy clipboard ends with a visible actionable failure");
  Assert(!reader.NeedsCapture(14,0),"Permanent failure does not create an endless background retry loop");
  reader.Begin(15); result=reader.Try(15,0,delegate { reads++; }); Assert(result.published,"Next user copy recovers after a prior permanent failure");
  reader.Begin(16); result=reader.Try(16,16,delegate { throw new Exception("echo"); }); Assert(!result.published && !result.failed && result.retryAfter==0,"Received clipboard write is suppressed to prevent echo");
  reader.Begin(17); result=reader.Try(17,0,delegate { throw new InvalidOperationException("paused"); }); Assert(result.failed && result.message.Contains("paused"),"Non-transient failure preserves its reason");
  Assert(reader.NeedsCapture(18,0),"Polling fallback can detect a new copy when no listener notification arrived");
  Console.WriteLine(checks+" clipboard capture checks passed");
 }
}
