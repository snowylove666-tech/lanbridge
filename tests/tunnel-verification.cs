using System;
using System.Reflection;
class TunnelVerification {
 static int Main(string[] args) {
  try {
   var assembly=Assembly.LoadFrom(args[0]);
   var method=assembly.GetType("LanBridge.RemoteConnection").GetMethod("VerifyPublicRoute",BindingFlags.Static|BindingFlags.NonPublic);
   method.Invoke(null,new object[]{args[1],args[2]}); return 0;
  } catch(Exception ex) { Console.WriteLine((ex.InnerException??ex).GetType().Name); return 1; }
 }
}
