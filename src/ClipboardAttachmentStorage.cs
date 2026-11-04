using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace LanBridge {
 public sealed class ClipboardAttachmentSelection {
  public string[] paths; public string kind; public bool staged;
 }
 // This class never calls Clipboard. Its IDataObject/image entry points can be
 // exercised with generated fixtures without reading the user's clipboard.
 public static class ClipboardAttachmentStorage {
  public const int MaxFiles=32;
  public const long MaxImageBytes=64L*1024*1024,MaxImagePixels=40000000L,MaxStagingBytes=256L*1024*1024;
  public static string[] ValidateFiles(IEnumerable<string> paths) {
   if(paths==null) throw new InvalidDataException("剪贴板里没有文件");
   var list=paths.ToArray(); if(list.Length==0 || list.Length>MaxFiles) throw new InvalidDataException("一次最多发送 32 个文件；文件夹请先压缩为 ZIP");
   for(int i=0;i<list.Length;i++) {
    if(String.IsNullOrWhiteSpace(list[i])) throw new InvalidDataException("文件路径无效");
    list[i]=Path.GetFullPath(list[i]);
    if(Directory.Exists(list[i])) throw new InvalidDataException("暂不直接粘贴文件夹，请先压缩为 ZIP："+Path.GetFileName(list[i]));
    if(!File.Exists(list[i])) throw new FileNotFoundException("文件已被移动或删除："+Path.GetFileName(list[i]));
    if(new FileInfo(list[i]).Length>1024L*1024*1024) throw new InvalidDataException("单个文件最大 1 GB："+Path.GetFileName(list[i]));
   } return list;
  }
  public static ClipboardAttachmentSelection Read(IDataObject data,string root) {
   if(data==null) return null;
   if(data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing",false)) throw new InvalidDataException("来源应用已标记此内容不可共享");
   if(data.GetDataPresent(DataFormats.FileDrop)) {
    var files=data.GetData(DataFormats.FileDrop) as string[];
    if(files==null) throw new InvalidDataException("剪贴板文件列表格式不可用，请重新复制文件");
    return new ClipboardAttachmentSelection {paths=ValidateFiles(files),kind="files"};
   }
   if(data.GetDataPresent("PNG",false)) {
    object raw=data.GetData("PNG",false); byte[] bytes=raw as byte[];
    var stream=raw as Stream;
    if(bytes==null && stream!=null) {
     long position=stream.CanSeek?stream.Position:0;
     try { if(stream.CanSeek) stream.Position=0; bytes=ReadLimited(stream); }
     finally { if(stream.CanSeek) stream.Position=position; }
    }
    if(bytes!=null) using(var image=ReadPng(bytes)) return StageImage(image,root);
   }
   if(data.GetDataPresent(DataFormats.Bitmap)) {
    var image=data.GetData(DataFormats.Bitmap) as Image;
    if(image==null) throw new InvalidDataException("剪贴板图片不可用，请重新截图或复制图片");
    // Do not dispose the source object owned by the clipboard provider.
    return StageImage(image,root);
   }
   if(data.GetDataPresent("FileGroupDescriptorW",false) || data.GetDataPresent("FileGroupDescriptor",false)) throw new InvalidDataException("这是应用内部的虚拟附件，请先保存或拖出为实际文件，再复制发送");
   return null;
  }
  static byte[] ReadLimited(Stream stream) {
   using(var output=new MemoryStream()) {
    byte[] buffer=new byte[65536]; int count;
    while((count=stream.Read(buffer,0,buffer.Length))>0) { if(output.Length+count>MaxImageBytes) throw new InvalidDataException("剪贴板图片超过 64 MB，请保存为文件发送"); output.Write(buffer,0,count); }
    return output.ToArray();
   }
  }
  static void ValidateImage(Image image) {
   if(image==null || image.Width<=0 || image.Height<=0 || image.Width>32767 || image.Height>32767 || (long)image.Width*image.Height>MaxImagePixels) throw new InvalidDataException("图片过大，剪贴板图片最多支持 4000 万像素，请保存为文件发送");
  }
  public static Bitmap ReadPng(byte[] bytes) {
   if(bytes==null || bytes.Length<8 || bytes.LongLength>MaxImageBytes || bytes[0]!=137 || bytes[1]!=80 || bytes[2]!=78 || bytes[3]!=71 || bytes[4]!=13 || bytes[5]!=10 || bytes[6]!=26 || bytes[7]!=10) throw new InvalidDataException("收到的图片不是有效 PNG；文件已保存，可手动打开查看");
   try { using(var stream=new MemoryStream(bytes,false)) using(var image=Image.FromStream(stream,true,true)) {
    if(image.RawFormat.Guid!=ImageFormat.Png.Guid) throw new InvalidDataException("收到的图片不是 PNG"); ValidateImage(image);
    var bitmap=new Bitmap(image.Width,image.Height,PixelFormat.Format32bppArgb); using(var graphics=Graphics.FromImage(bitmap)) { graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy; graphics.DrawImageUnscaled(image,0,0); } return bitmap;
   } } catch(ArgumentException ex) { throw new InvalidDataException("PNG 图片无法解码；文件已保存，可手动打开查看",ex); }
  }
  public static ClipboardAttachmentSelection StageImage(Image image,string root) {
   ValidateImage(image); string folder=Path.Combine(Path.GetFullPath(root),"Data","ClipboardStaging"); Directory.CreateDirectory(folder);
   long occupied=Directory.GetFiles(folder,"clip-*.png").Sum(p=>{try{return new FileInfo(p).Length;}catch{return 0L;}});
   if(occupied>=MaxStagingBytes) throw new InvalidDataException("图片临时区已满，请在传输完成后重启轻桥以清理旧图片");
   string path=Path.Combine(folder,"clip-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".png");
   try {
    using(var bitmap=new Bitmap(image.Width,image.Height,PixelFormat.Format32bppArgb)) {
     using(var graphics=Graphics.FromImage(bitmap)) { graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy; graphics.DrawImageUnscaled(image,0,0); }
     using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) bitmap.Save(output,ImageFormat.Png);
    }
    long size=new FileInfo(path).Length;
    if(size>MaxImageBytes || occupied+size>MaxStagingBytes) throw new InvalidDataException("剪贴板图片临时数据过大，请保存为文件发送");
    return new ClipboardAttachmentSelection {paths=new[]{path},kind="image",staged=true};
   } catch { try { File.Delete(path); } catch {} throw; }
  }
  // Call at startup before queuing uploads. Recent files are retained so an
  // asynchronous sender never loses a staged image it has not opened yet.
  public static void TrimStaging(string root) {
   string folder=Path.Combine(Path.GetFullPath(root),"Data","ClipboardStaging"); if(!Directory.Exists(folder)) return;
   foreach(string path in Directory.GetFiles(folder,"clip-*.png")) {
    if(!Regex.IsMatch(Path.GetFileName(path),@"\Aclip-[0-9]{8}-[0-9]{6}-[a-f0-9]{32}\.png\z")) continue;
    try { if(File.GetLastWriteTimeUtc(path)<DateTime.UtcNow.AddHours(-24)) File.Delete(path); } catch {}
   }
  }
  public static ClipboardAttachmentPayload BuildPayload(string[] paths,string kind) {
   paths=ValidateFiles(paths);
   if(kind!="image" && kind!="files") throw new InvalidDataException("不支持的剪贴板附件类型");
   var payload=new ClipboardAttachmentPayload();
   try {
    var names=new StringCollection(); names.AddRange(paths); payload.data.SetFileDropList(names);
    payload.AddStream("Preferred DropEffect",BitConverter.GetBytes(1)); // Copy, never move.
    if(kind=="image") {
     if(paths.Length!=1) throw new InvalidDataException("一份图片剪贴板只能包含一张 PNG 图片");
     var info=new FileInfo(paths[0]); if(info.Length>MaxImageBytes) throw new InvalidDataException("PNG 图片超过 64 MB，已保留为文件");
     byte[] png=File.ReadAllBytes(paths[0]); payload.bitmap=ReadPng(png); payload.data.SetData(DataFormats.Bitmap,true,payload.bitmap); payload.AddStream("PNG",png);
     using(var stream=new MemoryStream()) { payload.bitmap.Save(stream,ImageFormat.Bmp); byte[] bmp=stream.ToArray(); byte[] dib=new byte[bmp.Length-14]; Buffer.BlockCopy(bmp,14,dib,0,dib.Length); payload.AddStream(DataFormats.Dib,dib); }
    }
    return payload;
   } catch { payload.Dispose(); throw; }
  }
 }
 public sealed class ClipboardAttachmentPayload : IDisposable {
  public readonly DataObject data=new DataObject(); internal Bitmap bitmap; readonly List<MemoryStream> streams=new List<MemoryStream>();
  internal void AddStream(string format,byte[] bytes) { var stream=new MemoryStream(bytes,false); streams.Add(stream); data.SetData(format,false,stream); }
  public void Dispose() { if(bitmap!=null) bitmap.Dispose(); foreach(var stream in streams) stream.Dispose(); }
 }
}
