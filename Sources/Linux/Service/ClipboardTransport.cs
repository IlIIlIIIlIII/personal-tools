using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace PersonalTools;
public sealed record CaptureMime(string Mime,int FdIndex,long Length,string? Name=null);
public sealed record CaptureMetadata(long Generation,string SourceApp,string SourceAttribution,CaptureMime[] Mimes);
public sealed class ClipboardTransport(ClipboardStore store)
{
    const int MaxMimes=32;
    static readonly HashSet<string> Allowed=new(StringComparer.Ordinal){"text/plain","text/plain;charset=utf-8","text/plain;charset=UTF-8","UTF8_STRING","text/html","image/png","image/jpeg","image/webp","text/uri-list","x-special/gnome-copied-files"};
    readonly SemaphoreSlim captureGate=new(1);
    public static CaptureMetadata Validate(string metadata,SafeFileHandle[] fds,long limit)
    {
        if(Encoding.UTF8.GetByteCount(metadata)>1024*1024)throw new ArgumentException("Clipboard metadata exceeds 1 MiB.");
        var data=JsonSerializer.Deserialize<CaptureMetadata>(metadata,LinuxPaths.Json)??throw new ArgumentException("Clipboard metadata is empty.");
        if(data.SourceApp is null||data.SourceApp.Length>512||data.SourceAttribution is not("verified" or "foreground-observed" or "unknown"))throw new ArgumentException("Invalid source attribution.");
        if(data.Mimes is null||data.Mimes.Length is 0 or >MaxMimes||fds.Length!=data.Mimes.Length||data.Mimes.Select(x=>x.FdIndex).Distinct().Count()!=fds.Length||data.Mimes.Select(x=>x.Mime).Distinct().Count()!=data.Mimes.Length)throw new ArgumentException("Invalid clipboard MIME/FD table.");
        long total=0;
        foreach(var mime in data.Mimes){if(!Allowed.Contains(mime.Mime)||mime.FdIndex<0||mime.FdIndex>=fds.Length||mime.Length<0||mime.Length>limit)throw new ArgumentException("Unsupported MIME or invalid clipboard payload length.");total=checked(total+mime.Length);}
        if(total>limit||total>int.MaxValue)throw new ArgumentException("Clipboard item exceeds configured byte limit.");
        return data;
    }
    public async Task<ClipEntry?> CaptureAsync(string metadata,SafeFileHandle[] fds,CancellationToken token=default)
    {
        try {
            var data=Validate(metadata,fds,store.Settings.ItemLimitBytes);
            if(store.Settings.Paused||store.Settings.ExcludedApps.Any(x=>x.Equals(data.SourceApp,StringComparison.OrdinalIgnoreCase)))return null;
            await captureGate.WaitAsync(token);
            try {
                var payloads=new List<MimePayload>();
                foreach(var mime in data.Mimes){
                    // A pipe or socket could never finish and bypass length checks: accept seekable regular/memfd payloads only.
                    using var stream=new FileStream(fds[mime.FdIndex],FileAccess.Read,4096,false);
                    if(!stream.CanSeek||stream.Length!=mime.Length)throw new ArgumentException("Clipboard FD must be a regular file of the declared length.");
                    stream.Position=0;var bytes=new byte[checked((int)mime.Length)];await stream.ReadExactlyAsync(bytes,token);
                    payloads.Add(new(mime.Mime,bytes));
                }
                var files=payloads.FirstOrDefault(x=>x.Type is "text/uri-list" or "x-special/gnome-copied-files");
                ClipCapture capture;
                if(files is not null){
                    var list=Decode(files.Bytes).Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Where(x=>!x.StartsWith('#')&&x is not("copy" or "cut")).ToArray();
                    if(list.Length is 0 or >256)throw new ArgumentException("Copy between 1 and 256 regular files.");
                    var paths=list.Select(ValidateLocalFile).ToArray();
                    if(paths.Sum(x=>new FileInfo(x).Length)>store.Settings.ItemLimitBytes)throw new ArgumentException("File content exceeds item limit.");
                    // Do not archive source file URIs: restoration must refer to our preserved copies.
                    capture=new("Files",data.SourceApp,Files:paths,MimePayloads:payloads.Where(x=>x.Type is not("text/uri-list" or "x-special/gnome-copied-files")).ToArray(),SourceReliability:data.SourceAttribution);
                }else if((payloads.FirstOrDefault(x=>x.Type=="image/png")??payloads.FirstOrDefault(x=>x.Type.StartsWith("image/",StringComparison.Ordinal))) is { } image){
                    ValidateImage(image);capture=new("Image",data.SourceApp,Image:image.Bytes,MimePayloads:payloads.ToArray(),SourceReliability:data.SourceAttribution,ImageMime:image.Type);
                }else {
                    var text=payloads.FirstOrDefault(x=>x.Type.StartsWith("text/plain",StringComparison.Ordinal)||x.Type=="UTF8_STRING")??throw new ArgumentException("Clipboard has no supported text, image or file representation.");
                    capture=new("Text",data.SourceApp,Text:Decode(text.Bytes),MimePayloads:payloads.ToArray(),SourceReliability:data.SourceAttribution);
                }
                return await store.SaveAsync(capture,token);
            }finally{captureGate.Release();}
        }finally{foreach(var fd in fds)fd.Dispose();}
    }
    static string Decode(byte[] bytes)=>new UTF8Encoding(false,true).GetString(bytes).TrimEnd('\0');
    public static string ValidateLocalFile(string uriText)
    {
        if(!Uri.TryCreate(uriText,UriKind.Absolute,out var uri)||!uri.IsFile||!string.IsNullOrEmpty(uri.Host)&&uri.Host!="localhost")throw new ArgumentException("Only local file URIs are supported.");
        string path=Path.GetFullPath(uri.LocalPath);
        for(string? cursor=path;cursor is not null;cursor=Path.GetDirectoryName(cursor))if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("Symbolic links are not captured.");
        if(!File.Exists(path)||Directory.Exists(path))throw new ArgumentException("Only regular files are supported.");
        // Reject devices, FIFOs and sockets before opening; FileInfo alone treats them as files.
        if(NativeFile.IsRegular(path)==false)throw new ArgumentException("Only regular files are supported.");
        return path;
    }
    static void ValidateImage(MimePayload image)
    {
        bool valid=image.Type switch {
            "image/png"=>image.Bytes.Length>=24&&image.Bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
            "image/jpeg"=>image.Bytes.Length>=4&&image.Bytes[0]==255&&image.Bytes[1]==216&&image.Bytes[^2]==255&&image.Bytes[^1]==217,
            "image/webp"=>image.Bytes.Length>=12&&Encoding.ASCII.GetString(image.Bytes,0,4)=="RIFF"&&Encoding.ASCII.GetString(image.Bytes,8,4)=="WEBP",
            _=>false};
        if(!valid)throw new ArgumentException("Image signature does not match the declared MIME type.");
    }
    public (string Metadata,SafeFileHandle[] Fds) Open(string id)
    {
        var entry=store.Entries.FirstOrDefault(x=>x.Id==id)??throw new KeyNotFoundException("Clipboard entry was deleted.");
        var payloads=new List<MimePayload>();
        if(entry.Kind=="Files"){
            var files=entry.Attachments.Where(x=>x.RelativePath.StartsWith("files/",StringComparison.Ordinal)).Select(x=>new Uri(store.PayloadPath(entry,x)).AbsoluteUri).ToArray();
            payloads.Add(new("text/uri-list",Encoding.UTF8.GetBytes(string.Join("\r\n",files)+"\r\n")));
            payloads.Add(new("x-special/gnome-copied-files",Encoding.UTF8.GetBytes("copy\n"+string.Join("\n",files))));
        }
        foreach(var mime in entry.Mimes??[]){var path=ClipboardStore.SafePath(store.Root,Path.Combine(entry.RelativeDirectory,mime.RelativePath));payloads.Add(new(mime.Type,File.ReadAllBytes(path)));}
        if(payloads.Count==0){var attachment=entry.Attachments[0];payloads.Add(new(entry.Kind=="Image"?"image/png":"text/plain;charset=utf-8",File.ReadAllBytes(store.PayloadPath(entry,attachment))));}
        var fds=new List<SafeFileHandle>();
        try{
            foreach(var payload in payloads)fds.Add(NativeFile.Immutable(payload.Bytes));
            string metadata=JsonSerializer.Serialize(new{entryId=id,mimes=payloads.Select((p,index)=>new{mime=p.Type,fdIndex=index,length=p.Bytes.LongLength}).ToArray()},LinuxPaths.Json);
            return(metadata,fds.ToArray());
        }catch{foreach(var fd in fds)fd.Dispose();throw;}
    }
}
public static class NativeFile
{
    [DllImport("libc",SetLastError=true)]static extern int dup(int fd);
    [DllImport("libc",SetLastError=true)]static extern int memfd_create(string name,uint flags);
    [DllImport("libc",SetLastError=true)]static extern int fcntl(int fd,int command,int value);
    [DllImport("libc",SetLastError=true)]static extern int statx(int dirfd,string path,int flags,uint mask,byte[] buffer);
    public static IntPtr Duplicate(SafeHandle handle)
    {
        bool added=false;
        try { handle.DangerousAddRef(ref added); int result=dup(handle.DangerousGetHandle().ToInt32());if(result<0)throw new IOException("Cannot duplicate clipboard descriptor.");return (IntPtr)result; }
        finally { if(added)handle.DangerousRelease(); }
    }
    public static bool IsRegular(string path){var buffer=new byte[256];if(statx(-100,path,0x100,0x7ff,buffer)!=0)throw new IOException("Cannot inspect clipboard file.");return (BitConverter.ToUInt16(buffer,28)&0xf000)==0x8000;}
    public static SafeFileHandle Immutable(byte[] bytes)
    {
        int fd=memfd_create("personal-tools-clipboard",3);if(fd<0)throw new IOException("memfd_create failed.");
        using(var handle=new SafeFileHandle((IntPtr)fd,true)){
            using(var stream=new FileStream(handle,FileAccess.ReadWrite)){stream.Write(bytes);stream.Flush();
                if(fcntl(fd,1033,1|2|4|8)<0)throw new IOException("Clipboard payload sealing failed.");
                return File.OpenHandle($"/proc/self/fd/{fd}",FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            }
        }
    }
}
