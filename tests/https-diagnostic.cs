using System;
using System.IO;
using System.Net;
class HttpsDiagnostic {
 static void Main(string[] args) {
  Console.OutputEncoding=System.Text.Encoding.UTF8;
  ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
  foreach(string url in args) foreach(bool proxy in new[]{true,false}) {
   try {
    var request=(HttpWebRequest)WebRequest.Create(url); request.Timeout=8000; request.ReadWriteTimeout=8000; request.AllowAutoRedirect=false; if(!proxy) request.Proxy=null;
    using(var response=(HttpWebResponse)request.GetResponse()) using(var stream=response.GetResponseStream()) using(var reader=new StreamReader(stream)) { string text=reader.ReadToEnd(); Console.WriteLine(url+" proxy="+proxy+" HTTP="+(int)response.StatusCode+" "+text.Substring(0,Math.Min(200,text.Length))); }
   } catch(Exception ex) { Console.WriteLine(url+" proxy="+proxy+" ERROR="+ex.ToString()); }
  }
 }
}
