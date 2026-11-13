using System.IO;

namespace LanBridge;

/// <summary>服务器版入口：只当常驻中转 + 小云。参数同 LanBridge.exe --relay-server（--port --bind --pool --pool-mb --relay-max-mb --log）。</summary>
static class Program {
 public const string Version = "2.2.21";
 static int Main(string[] args) {
  System.Net.ServicePointManager.DefaultConnectionLimit = 64;
  return RelayHost.Run(args);
 }
}

static class Embedded {
 public static byte[] Resource(string name) {
  using var st = typeof(Embedded).Assembly.GetManifestResourceStream(name);
  if (st == null) return null; using var ms = new MemoryStream(); st.CopyTo(ms); return ms.ToArray();
 }
}
