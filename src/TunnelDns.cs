using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace LanBridge {
 // Only public Cloudflare discovery names are queried; clipboard data and
 // device credentials never enter a DNS request. Used after native DNS fails.
 static class TunnelDns {
  static object[] Query(string name,string type) {
   ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
   var request=(HttpWebRequest)WebRequest.Create("https://dns.alidns.com/resolve?name="+Uri.EscapeDataString(name)+"&type="+type);
   request.Timeout=8000; request.ReadWriteTimeout=8000; request.Accept="application/dns-json"; request.UserAgent="LanBridge/1.2";
   using(var response=request.GetResponse()) using(var stream=response.GetResponseStream()) using(var body=new MemoryStream()) {
    byte[] bytes=new byte[4096]; int n;
    while((n=stream.Read(bytes,0,bytes.Length))>0) { if(body.Length+n>65536) throw new InvalidDataException("DNS 响应过大"); body.Write(bytes,0,n); }
    var data=BridgeState.Json().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(body.ToArray()));
    object status,answer;
    if(!data.TryGetValue("Status",out status) || Convert.ToInt32(status)!=0 || !data.TryGetValue("Answer",out answer)) throw new InvalidDataException("公开隧道域名解析失败");
    var array=answer as System.Collections.IEnumerable; if(array==null) throw new InvalidDataException("DNS 响应格式错误");
    return array.Cast<object>().Take(32).ToArray();
   }
  }
  public static string Resolve() {
   var addresses=new List<string>();
   foreach(object item in Query("_v2-origintunneld._tcp.argotunnel.com","SRV")) {
    var record=item as Dictionary<string,object>; if(record==null || !record.ContainsKey("type") || Convert.ToInt32(record["type"])!=33) continue;
    string[] fields=Convert.ToString(record["data"]).Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
    if(fields.Length!=4 || fields[2]!="7844") continue;
    string host=fields[3].TrimEnd('.').ToLowerInvariant();
    if(host!="region1.v2.argotunnel.com" && host!="region2.v2.argotunnel.com") continue;
    foreach(object a in Query(host,"A")) {
     var address=a as Dictionary<string,object>; if(address==null || !address.ContainsKey("type") || Convert.ToInt32(address["type"])!=1) continue;
     IPAddress ip; if(!IPAddress.TryParse(Convert.ToString(address["data"]),out ip) || ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork || BridgeState.PrivateAddress(ip)) continue;
     byte[] b=ip.GetAddressBytes(); if(b[0]==0 || b[0]>=224 || (b[0]==100 && b[1]>=64 && b[1]<=127)) continue;
     if(!addresses.Contains(ip.ToString())) addresses.Add(ip.ToString());
     if(addresses.Count>=4) break;
    }
    if(addresses.Count>=4) break;
   }
   if(addresses.Count==0) throw new InvalidDataException("未获取到可用的公开隧道节点");
   return String.Join("",addresses.Select(x=>" --edge "+x+":7844").ToArray());
  }
 }
}
