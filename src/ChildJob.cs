using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LanBridge {
 // Windows closes the child process even if the parent is force terminated.
 sealed class ChildJob : SafeHandleZeroOrMinusOneIsInvalid {
  [StructLayout(LayoutKind.Sequential)] struct Basic { public long processTime,jobTime; public uint flags; public UIntPtr minWorking,maxWorking; public uint processes; public UIntPtr affinity; public uint priority,scheduling; }
  [StructLayout(LayoutKind.Sequential)] struct Io { public ulong readOps,writeOps,otherOps,readBytes,writeBytes,otherBytes; }
  [StructLayout(LayoutKind.Sequential)] struct Limits { public Basic basic; public Io io; public UIntPtr processMemory,jobMemory,peakProcess,peakJob; }
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes,string name);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int kind,ref Limits data,uint size);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  public ChildJob(Process process):base(true) {
   SetHandle(CreateJobObject(IntPtr.Zero,null)); if(IsInvalid) throw new Win32Exception();
   var limits=new Limits { basic=new Basic { flags=0x2000 } };
   if(!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf(typeof(Limits))) || !AssignProcessToJobObject(handle,process.Handle)) { int error=Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error,"无法启用跨网模块退出保护"); }
  }
  protected override bool ReleaseHandle() { return CloseHandle(handle); }
 }
}
