using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LanBridge;

static class ClipboardAttachmentTests {
 static int checks;
 static void Check(bool value,string message) { if(!value) throw new Exception(message); checks++; Console.WriteLine("PASS "+message); }
 static void Reject(Action action,string message) { bool rejected=false; try {action();} catch(InvalidDataException){rejected=true;} catch(FileNotFoundException){rejected=true;} Check(rejected,message); }
 [STAThread] static void Main(string[] args) {
  string root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
  string first=Path.Combine(root,"first.txt"),second=Path.Combine(root,"second.bin"); File.WriteAllText(first,"generated attachment"); File.WriteAllBytes(second,new byte[]{0,1,254,255});
  Check(ClipboardAttachmentStorage.Read(null,root)==null,"Empty data does not invent a clipboard attachment");
  var plain=new DataObject();plain.SetText("generated text");Check(ClipboardAttachmentStorage.Read(plain,root)==null,"Ordinary text remains in the text flow");
  var files=new DataObject();files.SetData(DataFormats.FileDrop,new[]{first,second});var selected=ClipboardAttachmentStorage.Read(files,root);
  Check(selected.kind=="files" && !selected.staged && selected.paths.SequenceEqual(new[]{first,second}),"Actual copied files keep order and are not staged as images");
  Reject(delegate {ClipboardAttachmentStorage.ValidateFiles(new[]{root});},"Folders are rejected with a save-as-ZIP fallback");
  Reject(delegate {ClipboardAttachmentStorage.ValidateFiles(new[]{Path.Combine(root,"missing.bin")});},"Moved or deleted source files are rejected");
  Reject(delegate {ClipboardAttachmentStorage.ValidateFiles(Enumerable.Repeat(first,33));},"More than 32 clipboard files are rejected");
  var excluded=new DataObject();excluded.SetData("ExcludeClipboardContentFromMonitorProcessing",true);excluded.SetData(DataFormats.FileDrop,new[]{first});
  Reject(delegate {ClipboardAttachmentStorage.Read(excluded,root);},"Source applications' no-monitor marker is respected");
  var virtualAttachment=new DataObject();virtualAttachment.SetData("FileGroupDescriptorW",new byte[4]);
  Reject(delegate {ClipboardAttachmentStorage.Read(virtualAttachment,root);},"Virtual app attachments require a real saved file");
  string imagePath;
  using(var image=new Bitmap(2,2,PixelFormat.Format32bppArgb)) {
   image.SetPixel(0,0,Color.FromArgb(70,10,40,90));image.SetPixel(1,0,Color.Red);image.SetPixel(0,1,Color.Green);image.SetPixel(1,1,Color.Blue);
   var bitmapData=new DataObject();bitmapData.SetData(DataFormats.Bitmap,image);var staged=ClipboardAttachmentStorage.Read(bitmapData,root);imagePath=staged.paths[0];
   Check(staged.staged && staged.kind=="image" && imagePath.StartsWith(Path.Combine(root,"Data","ClipboardStaging")),"Screenshot bitmap becomes an owned PNG inside isolated staging");
   Check(image.GetPixel(0,0).A==70,"Reading a provider-owned bitmap does not dispose or alter it");
   using(var decoded=ClipboardAttachmentStorage.ReadPng(File.ReadAllBytes(imagePath)))Check(decoded.Width==2 && decoded.Height==2 && decoded.GetPixel(0,0).A==70,"Staged PNG keeps size and transparency");
   using(var stream=new MemoryStream(File.ReadAllBytes(imagePath))) {
    stream.Position=3;var pngData=new DataObject();pngData.SetData("PNG",false,stream);var png=ClipboardAttachmentStorage.Read(pngData,root);
    Check(png.kind=="image" && File.Exists(png.paths[0]) && stream.Position==3,"PNG stream is decoded without changing the provider stream position");
   }
  }
  using(var payload=ClipboardAttachmentStorage.BuildPayload(new[]{imagePath},"image")) {
   Check(payload.data.GetDataPresent(DataFormats.FileDrop) && payload.data.GetFileDropList()[0]==imagePath,"Image clipboard payload also supports Explorer paste as a file");
   Check(payload.data.GetDataPresent(DataFormats.Bitmap) && payload.data.GetDataPresent("PNG",false) && payload.data.GetDataPresent(DataFormats.Dib,false),"Image payload offers Bitmap, PNG and DIB application formats");
   var effect=(Stream)payload.data.GetData("Preferred DropEffect",false);Check(effect.ReadByte()==1,"Received clipboard content requests copy rather than move");
  }
  using(var payload=ClipboardAttachmentStorage.BuildPayload(new[]{first,second},"files"))Check(payload.data.GetFileDropList().Cast<string>().SequenceEqual(new[]{first,second}) && !payload.data.GetDataPresent("PNG",false),"File payload preserves all types and order without pretending to be an image");
  Reject(delegate {using(var payload=ClipboardAttachmentStorage.BuildPayload(new[]{first},"image")){}},"Corrupt image data cannot be put into an image clipboard payload");
  Reject(delegate {using(var payload=ClipboardAttachmentStorage.BuildPayload(new[]{imagePath,imagePath},"image")){}},"A screenshot event cannot contain multiple image payloads");
  Reject(delegate {ClipboardAttachmentStorage.ReadPng(new byte[]{137,80,78,71,13,10,26,10});},"PNG magic alone is insufficient without a decodable image");
  var mixed=new DataObject();mixed.SetData(DataFormats.FileDrop,new[]{imagePath});mixed.SetData("PNG",false,File.ReadAllBytes(imagePath));
  Check(ClipboardAttachmentStorage.Read(mixed,root).kind=="files","An Explorer file-copy remains a file event even if an image format is also offered");
  string folder=Path.Combine(root,"Data","ClipboardStaging"),old=Path.Combine(folder,"clip-20000101-000000-"+new string('a',32)+".png"),unrelated=Path.Combine(folder,"clip-personal.png");
  File.WriteAllText(old,"generated old staging");File.WriteAllText(unrelated,"unrelated file");File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddHours(-25));File.SetLastWriteTimeUtc(unrelated,DateTime.UtcNow.AddHours(-25));
  ClipboardAttachmentStorage.TrimStaging(root);Check(!File.Exists(old) && File.Exists(unrelated) && File.Exists(imagePath),"Startup cleanup removes only old controlled staging names and preserves recent or unrelated files");
  string quota=Path.Combine(folder,"clip-20000101-000000-"+new string('b',32)+".png");using(var stream=File.Create(quota))stream.SetLength(ClipboardAttachmentStorage.MaxStagingBytes);
  try {using(var image=new Bitmap(1,1))Reject(delegate {ClipboardAttachmentStorage.StageImage(image,root);},"A full staging quota rejects a new image before creating an upload");} finally {File.Delete(quota);}
  Console.WriteLine(checks+" attachment storage checks passed; no system clipboard APIs were called.");
 }
}
