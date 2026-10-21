using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PersonalTools;
using Tmds.DBus;
[assembly:SupportedOSPlatform("linux")]
internal static class Program
{
    static int count;
    static void Assert(bool condition,string description){if(!condition)throw new Exception(description);count++;Console.WriteLine("PASS "+description);}
    static JsonElement Json(string text)=>JsonDocument.Parse(text).RootElement.Clone();
    static string Encode(object value)=>JsonSerializer.Serialize(value,LinuxPaths.Json);
    static async Task Main(string[] args)
    {
        if(args.Contains("--live-antigravity")){await LiveAntigravity.RunAsync();return;}
        if(args.Contains("--live-desktop")){await LiveDesktop.RunAsync(args.Contains("--exercise-hdr"));return;}
        if(args.Contains("--live-autostart")){await LiveAutostart.RunAsync();return;}
        int restoreHdr=Array.IndexOf(args,"--restore-hdr");if(restoreHdr>=0){await LiveDesktop.RestoreAsync(args[restoreHdr+1]);return;}
        var temp=Path.Combine(Path.GetTempPath(),"pt-svc-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        foreach(var name in new[]{"XDG_CONFIG_HOME","XDG_DATA_HOME","XDG_STATE_HOME","XDG_CACHE_HOME","XDG_RUNTIME_DIR"})Environment.SetEnvironmentVariable(name,Path.Combine(temp,name));
        LinuxPaths.Ensure();
        try{await Archive();await Bridge();Hdr();await Audio();await AudioConnectionChecks.RunAsync(Assert);await Shortcuts();await Dbus();Console.WriteLine($"All {count} checks passed.");}finally{Directory.Delete(temp,true);}
    }
    static async Task Archive()
    {
        var store=new ClipboardStore(new());await store.LoadAsync();var transport=new ClipboardTransport(store);
        byte[] plain=Encoding.UTF8.GetBytes("우분투 테스트 https://example.com"),html=Encoding.UTF8.GetBytes("<b>우분투 테스트</b>");
        var fds=new[]{NativeFile.Immutable(plain),NativeFile.Immutable(html)};
        var metadata=new CaptureMetadata(1,"org.example.Test","verified",[new("text/plain;charset=utf-8",0,plain.Length),new("text/html",1,html.Length)]);
        var entry=await transport.CaptureAsync(Encode(metadata),fds);Assert(entry is not null&&store.Entries.Count==1,"UTF-8/multiple MIME capture");
        Assert(fds.All(x=>x.IsClosed),"input FDs closed after ownership transfer");
        var restored=transport.Open(entry!.Id);using var info=JsonDocument.Parse(restored.Metadata);
        Assert(info.RootElement.GetProperty("mimes").GetArrayLength()==2,"all MIME formats preserved");
        using(var stream=new FileStream(restored.Fds[0],FileAccess.Read)){var bytes=new byte[plain.Length];stream.ReadExactly(bytes);Assert(bytes.SequenceEqual(plain),"sealed restore payload byte-for-byte");}
        restored.Fds[1].Dispose();
        var handle=NativeFile.Immutable(plain);bool denied=false;try{using var writable=new FileStream(handle,FileAccess.Write);writable.WriteByte(0);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){denied=true;}finally{handle.Dispose();}Assert(denied,"restore FD cannot be written");
        var category=await store.SaveCategoryAsync("업무");await store.SetCategoriesAsync(entry.Id,[category.Id]);await store.SetPinnedAsync(entry,true);
        var restarted=new ClipboardStore(store.Settings);await restarted.LoadAsync();Assert(restarted.Entries.Single().Pinned&&restarted.CategoryIds(entry.Id).Single()==category.Id,"pin/collection survive restart");
        Assert((await store.FindAsync("우분투",new ClipboardFilter{CategoryId=category.Id,PinnedOnly=true},default)).Length==1,"query filters apply to persisted archive");
        await store.SetImageTagsAsync(entry.Id,entry.ContentHash,new(["diagram"],"diagram description","recognized text","test",DateTimeOffset.UtcNow),"complete");await restarted.LoadAsync();Assert(restarted.Entries.Single().ImageTags?.Text=="recognized text","image metadata uses consistent JSON casing");
        var before=store.Entries.Count;var oversized=NativeFile.Immutable(plain);bool rejected=false;try{await transport.CaptureAsync(Encode(metadata with{Mimes=[new("text/plain",0,plain.Length+1)]}),[oversized]);}catch(ArgumentException){rejected=true;}Assert(rejected&&oversized.IsClosed&&store.Entries.Count==before,"bad FD length rejected atomically");
        var symlink=Path.Combine(LinuxPaths.Data,"link");File.CreateSymbolicLink(symlink,store.PayloadPath(entry,entry.Attachments[0]));try{ClipboardTransport.ValidateLocalFile(new Uri(symlink).AbsoluteUri);throw new Exception("Symlink accepted");}catch(ArgumentException){Assert(true,"file URI symlink rejected");}
        var filePath=Path.Combine(LinuxPaths.Data,"문서.txt");File.WriteAllText(filePath,"preserved file");var uris=Encoding.UTF8.GetBytes(new Uri(filePath).AbsoluteUri+"\r\n");
        var fileEntry=await transport.CaptureAsync(Encode(new CaptureMetadata(2,"org.gnome.Nautilus","foreground-observed",[new("text/uri-list",0,uris.Length)])),[NativeFile.Immutable(uris)]);File.Delete(filePath);
        var copied=transport.Open(fileEntry!.Id);using(var reader=new StreamReader(new FileStream(copied.Fds[0],FileAccess.Read))){var uri=(await reader.ReadToEndAsync()).Trim();Assert(File.ReadAllText(new Uri(uri).LocalPath)=="preserved file","file restore points to archive after original deletion");}foreach(var fd in copied.Fds)fd.Dispose();
        await store.UpdateSettingsAsync(store.Settings with{Paused=true});var ignored=await transport.CaptureAsync(Encode(new CaptureMetadata(3,"test","unknown",[new("text/plain",0,plain.Length)])),[NativeFile.Immutable(plain)]);Assert(ignored is null,"paused capture does not archive");await store.UpdateSettingsAsync(store.Settings with{Paused=false});
        string outside=Path.Combine(LinuxPaths.Data,"Archive"),inside=Path.Combine(LinuxPaths.Data,"archive");Directory.CreateDirectory(outside);Directory.CreateDirectory(inside);bool caseBlocked=false;try{ClipboardStore.SafePath(outside,"../archive/payload");}catch(InvalidDataException){caseBlocked=true;}Assert(caseBlocked,"Linux archive boundary is case-sensitive");
    }
    static async Task Bridge()
    {
        string cli=Path.Combine(LinuxPaths.Data,"fake-agy");
        await File.WriteAllTextAsync(cli,"#!/bin/sh\nif [ \"$1\" = models ]; then printf 'gemini-test\\tTest model\\n'; exit 0; fi\nprintf '%s\\n' '{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"hello\"}}' '{\"result\":{\"status\":\"SUCCESS\",\"response\":\"hello\",\"usage\":{\"input_tokens\":3,\"output_tokens\":1,\"total_tokens\":4}}}'\n");File.SetUnixFileMode(cli,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        using var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var settings=new BridgeSettings(cli,port,"gemini-test",10);var models=await AntigravityBridge.GetModelsAsync(settings,default);Assert(models.Contains("gemini-test"),"real CLI child model discovery parser");
        await using var bridge=new AntigravityBridge("test-secret");await bridge.StartAsync(settings);
        using var http=new HttpClient();var response=await http.GetAsync($"http://127.0.0.1:{port}/v1/models");Assert(response.StatusCode==HttpStatusCode.Unauthorized,"bridge requires bearer authentication");
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","test-secret");
        var result=await http.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions",new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"stream\":false}",Encoding.UTF8,"application/json"));
        var body=Json(await result.Content.ReadAsStringAsync());Assert(result.IsSuccessStatusCode&&body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()=="hello","HTTP bridge calls actual test CLI and maps response");
        var streamed=await http.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions",new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"stream\":true}",Encoding.UTF8,"application/json"));var sse=await streamed.Content.ReadAsStringAsync();Assert(sse.Contains("[DONE]")&&sse.Contains("hello"),"SSE chunks and completion framing");
        await bridge.StopAsync();Assert(!bridge.IsRunning,"bridge process lifecycle stops listener");
    }
    sealed class Display:IHdrDisplay{public bool Enabled;public int Changes;public HdrMonitor[] Read()=>[new("monitor","test",true,true,Enabled)];public void Set(string id,bool value){Enabled=value;Changes++;}}
    sealed class Journal:IHdrJournal{public List<HdrSession> Sessions=[];public List<HdrSession> Load()=>Sessions;public void Save(List<HdrSession> value)=>Sessions=value;}
    static void Hdr(){var display=new Display();var journal=new Journal();var auto=new HdrAutomation(display,journal);var rule=new HdrProgram(Guid.NewGuid(),"Game","/tmp/Game");auto.Reconcile([new("1:100","/tmp/Game")],[rule]);Assert(display.Enabled&&journal.Sessions.Count==1,"HDR owns primary only after durable intent");auto.Reconcile([new("2:100","/tmp/Game")],[rule]);Assert(display.Enabled,"process replacement restores/reacquires ownership");auto.SetManual("monitor",false);auto.Reconcile([],[]);Assert(!display.Enabled&&journal.Sessions.Count==0,"manual HDR override wins over automatic restore");auto.Reconcile([new("3:200","/tmp/game")],[rule]);Assert(!display.Enabled,"executable matching is case-sensitive");}
    static async Task Audio()
    {
        var path=Path.Combine(LinuxPaths.Runtime,"audio.sock");
        using var listener=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);listener.Bind(new UnixDomainSocketEndPoint(path));listener.Listen(8);
        LinuxPaths.Write(Path.Combine(LinuxPaths.Config,"audio.json"),new{outputId="unplugged-saved-device",eqEnabled=true,muted=true,leftFile="preserved-filter.txt"});
        var requests=new List<string>();bool engineMuted=true;
        var server=Task.Run(async()=>{for(int i=0;i<4;i++){using var socket=await listener.AcceptAsync();using var stream=new NetworkStream(socket);using var reader=new StreamReader(stream);var line=await reader.ReadLineAsync();using var request=JsonDocument.Parse(line!);var value=request.RootElement;requests.Add(value.GetProperty("command").GetString()!);if(requests[^1]=="mute"){engineMuted=value.GetProperty("args").GetProperty("muted").GetBoolean();Assert(!engineMuted,"startup ignores saved mute=true and explicitly unmutes");}var data=new{status="ready",eqEnabled=true,muted=engineMuted,leftFilters=Array.Empty<object>(),rightFilters=Array.Empty<object>()};var response=JsonSerializer.Serialize(new{id=value.GetProperty("id").GetString(),ok=true,data})+"\n";await stream.WriteAsync(Encoding.UTF8.GetBytes(response));}});
        var audio=new AudioClient();await audio.RefreshAsync(restore:false);await audio.RefreshAsync();
        // A read-only snapshot must not mark persisted settings as restored.
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(requests.SequenceEqual(new[]{"status","mute","restore","status"}),"audio read-only snapshot and restore/mute replay use one JSON line per command");
        Assert(Json(Encode(audio.Snapshot)).GetProperty("leftFile").GetString()=="preserved-filter.txt","daemon retains imported EQ path after engine restart");
        Assert(!Json(Encode(audio.Snapshot)).GetProperty("muted").GetBoolean(),"startup snapshot confirms microphone unmuted");
        Assert(!LinuxPaths.Read(Path.Combine(LinuxPaths.Config,"audio.json"),new Dictionary<string,JsonElement>())["muted"].GetBoolean(),"startup replaces stale saved mute for later recovery");
        var settings=Path.Combine(LinuxPaths.Config,"audio.json");
        var filters=new[]{new{frequency=66.0,gain=-12.0,q=7.4,enabled=true}};
        LinuxPaths.Write(settings,new{eqEnabled=true,leftFilters=filters,rightFilters=filters,muted=true});
        var emptyEngine=Task.Run(async()=>{
            using var socket=await listener.AcceptAsync();using var stream=new NetworkStream(socket);using var reader=new StreamReader(stream);
            using var request=JsonDocument.Parse((await reader.ReadLineAsync())!);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id=request.RootElement.GetProperty("id").GetString(),ok=true,data=new{muted=false,leftFilters=Array.Empty<object>(),rightFilters=Array.Empty<object>()}})+"\n"));
        });
        await audio.PersistAsync("mute",JsonSerializer.SerializeToElement(new{muted=false}));await emptyEngine;
        var saved=LinuxPaths.Read(settings,new Dictionary<string,JsonElement>());
        Assert(saved["leftFilters"].GetArrayLength()==1&&saved["rightFilters"].GetArrayLength()==1&&saved["eqEnabled"].GetBoolean()&&!saved["muted"].GetBoolean(),"mute during engine recovery preserves EQ filters and updates only mute");
        File.Delete(path);
    }
    sealed class ShortcutShell : IPersonalToolsShell
    {
        public ObjectPath ObjectPath => new("/io/griplabs/PersonalTools/Shell");
        public AppSettings Settings = new();
        public int Requests;
        public bool Reject;
        public Task ShowClipboardAsync()=>Task.CompletedTask;
        public Task<string> PutClipboardAsync(string id,bool paste)=>Task.FromResult("{}");
        public Task<string> GetStatusAsync()=>Task.FromResult("{}");
        public Task<string> ConfigureShortcutsAsync(string json)
        {
            Requests++;
            if(Reject)return Task.FromResult("{\"ok\":false,\"error\":\"conflict\"}");
            var value=Json(json);
            Settings=Settings with{ClipboardShortcut=value.GetProperty("clipboardShortcut").GetString()!,MicrophoneShortcut=value.GetProperty("microphoneShortcut").GetString()!};
            return Task.FromResult("{\"ok\":true}");
        }
    }
    static async Task Shortcuts()
    {
        Assert(new AppSettings().ClipboardShortcut=="<Control><Shift>v"&&new ClipboardSettings().Hotkey=="<Control><Shift>v","Ctrl+Shift+V is the app and portable clipboard default");
        Assert(ShortcutConfiguration.Canonical("<Shift><Ctrl>V")=="<Control><Shift>v","shortcut modifier order and Ctrl alias normalize consistently");
        using var shellConnection=new Connection(Address.Session);await shellConnection.ConnectAsync();
        var shell=new ShortcutShell();await shellConnection.RegisterObjectAsync(shell);await shellConnection.RegisterServiceAsync("io.griplabs.PersonalTools.Shell");
        using var daemonConnection=new Connection(Address.Session);await daemonConnection.ConnectAsync();await using var daemon=new Daemon(daemonConnection);
        string path=Path.Combine(LinuxPaths.Config,"settings.json");
        async Task<JsonElement> Update(string value)=>Json(await daemon.ExecuteAsync("settings","update",value).WaitAsync(TimeSpan.FromSeconds(3)));
        var first=await Update("{\"clipboardShortcut\":\"<Control><Alt>v\"}");
        Assert(first.GetProperty("ok").GetBoolean()&&shell.Requests==1&&File.Exists(path),"shortcut update registers once then saves without recursive settings command");
        var controlShift=await Update("{\"clipboardShortcut\":\"<Shift><Control>V\"}");
        Assert(controlShift.GetProperty("ok").GetBoolean()&&shell.Settings.ClipboardShortcut=="<Control><Shift>v"&&LinuxPaths.Read(path,new AppSettings()).ClipboardShortcut=="<Control><Shift>v","Ctrl+Shift+V is registered and persisted canonically");
        string original=File.ReadAllText(path);int countBefore=shell.Requests;
        var duplicate=await Update("{\"clipboardShortcut\":\"<Alt><Super>M\"}");
        Assert(!duplicate.GetProperty("ok").GetBoolean()&&shell.Requests==countBefore&&File.ReadAllText(path)==original,"microphone alias collision is rejected before registration or persistence");
        var invalid=await Update("{\"clipboardShortcut\":\"<Control><Ctrl>v\"}");
        Assert(!invalid.GetProperty("ok").GetBoolean()&&shell.Requests==countBefore&&File.ReadAllText(path)==original,"duplicate modifiers are rejected without altering saved shortcuts");
        shell.Reject=true;
        var conflict=await Update("{\"clipboardShortcut\":\"<Control><Alt>v\"}");
        Assert(!conflict.GetProperty("ok").GetBoolean()&&File.ReadAllText(path)==original&&shell.Settings.ClipboardShortcut=="<Control><Shift>v","Shell conflict failure keeps the previous shortcut and saved settings");
        shell.Reject=false;File.Delete(path);Directory.CreateDirectory(path);
        try
        {
            countBefore=shell.Requests;
            var saveFailure=await Update("{\"clipboardShortcut\":\"<Control><Alt>v\"}");
            Assert(!saveFailure.GetProperty("ok").GetBoolean()&&shell.Requests==countBefore+2&&shell.Settings.ClipboardShortcut=="<Control><Shift>v","settings write failure rolls the registered Shell keys back");
            Assert(Json(await daemon.GetSnapshotAsync()).GetProperty("settings").GetProperty("clipboardShortcut").GetString()=="<Control><Shift>v","failed shortcut save never changes the service snapshot");
        }
        finally{Directory.Delete(path);File.WriteAllText(path,original);}
    }
    static async Task Dbus()
    {
        using var server=new Connection(Address.Session);await server.ConnectAsync();await using var daemon=new Daemon(server);
        await daemon.InitializeAsync(false);
        using var bus=new Tmds.DBus.Protocol.DBusConnection(Tmds.DBus.Protocol.DBusAddress.Session!);await bus.ConnectAsync();using var host=new BusHost(bus,daemon);await host.RegisterAsync("io.griplabs.PersonalTools.Tests");
        using var client=new Tmds.DBus.Protocol.DBusConnection(Tmds.DBus.Protocol.DBusAddress.Session!);await client.ConnectAsync();var proxy=new PersonalTools.Tests.Bus.PersonalTools1(client,"io.griplabs.PersonalTools.Tests","/io/griplabs/PersonalTools");
        var snapshot=Json(await proxy.GetSnapshotAsync());Assert(snapshot.GetProperty("version").GetInt32()==1,"D-Bus snapshot contract");
        var signals=0;using var watch=await proxy.WatchClipboardChangedAsync(()=>Interlocked.Increment(ref signals));
        byte[] bytes=Encoding.UTF8.GetBytes("D-Bus Unix-FD integration payload");
        using var sent=NativeFile.Immutable(bytes);
        var captured=Json(await proxy.CaptureClipboardAsync(Encode(new CaptureMetadata(50,"test","verified",[new("text/plain",0,bytes.Length)])),[sent]));Assert(captured.GetProperty("ok").GetBoolean(),"D-Bus ah request carries actual Unix FD");
        var id=captured.GetProperty("data").GetProperty("entry").GetProperty("id").GetString()!;
        var restored=await proxy.OpenClipboardAsync(id);using(var stream=new FileStream((SafeFileHandle)restored.PayloadFds[0],FileAccess.Read)){var read=new byte[bytes.Length];stream.ReadExactly(read);Assert(read.SequenceEqual(bytes),"D-Bus ah response survives server ownership release");}
        for(int i=0;i<20&&signals==0;i++)await Task.Delay(20);Assert(signals>0,"ClipboardChanged D-Bus signal emitted");
        var invalid=Json(await proxy.ExecuteAsync("clipboard","pin","{\"id\":\"missing\",\"pinned\":true}"));Assert(!invalid.GetProperty("ok").GetBoolean(),"invalid action is explicit failure");
        int readSignals=0;using var readWatch=await daemon.WatchChangedAsync(_=>Interlocked.Increment(ref readSignals));
        var query=Json(await proxy.ExecuteAsync("clipboard","query","{\"query\":\"Unix-FD\",\"filter\":{}}"));Assert(query.GetProperty("data").GetProperty("entries").GetArrayLength()==1,"D-Bus archive search integration");
        Assert(readSignals==0,"read-only query never feeds back a Changed snapshot");
        var detail=Json(await proxy.ExecuteAsync("clipboard","detail",Encode(new{id}))).GetProperty("data");
        Assert(readSignals==0&&detail.GetProperty("text").GetString()=="D-Bus Unix-FD integration payload"&&detail.GetProperty("entry").TryGetProperty("description",out _),"detail returns original text/image description without Changed feedback");
        var paged=Json(await proxy.ExecuteAsync("clipboard","query","{\"query\":\"Unix-FD\",\"offset\":1,\"limit\":1}"));Assert(paged.GetProperty("data").GetProperty("entries").GetArrayLength()==0&&paged.GetProperty("data").GetProperty("total").GetInt32()==1,"clipboard pagination preserves filtered total");
        var badPage=Json(await proxy.ExecuteAsync("clipboard","query","{\"offset\":-1}"));Assert(!badPage.GetProperty("ok").GetBoolean(),"invalid clipboard pagination rejected");
        byte[] largeText=Encoding.UTF8.GetBytes(new string('가',200000));using var largeFd=NativeFile.Immutable(largeText);
        var largeCapture=Json(await proxy.CaptureClipboardAsync(Encode(new CaptureMetadata(51,"test","verified",[new("text/plain;charset=utf-8",0,largeText.Length)])),[largeFd]));
        string largeId=largeCapture.GetProperty("data").GetProperty("entry").GetProperty("id").GetString()!;
        var largeDetail=Json(await proxy.ExecuteAsync("clipboard","detail",Encode(new{id=largeId}))).GetProperty("data");
        Assert(largeDetail.GetProperty("textTruncated").GetBoolean()&&largeDetail.GetProperty("text").GetString()!.Length==65536&&File.ReadAllBytes(largeDetail.GetProperty("textPath").GetString()!).SequenceEqual(largeText),"large Unicode text detail is bounded while complete archived content remains available");
        var exported=Json(await proxy.ExecuteAsync("diagnostics","export","{}"));var report=File.ReadAllText(exported.GetProperty("data").GetProperty("path").GetString()!);
        Assert(!report.Contains("Unix-FD integration payload")&&!report.Contains("apiKey")&&!report.Contains("preview")&&!report.Contains("categoryIds"),"diagnostic export excludes clipboard content and API keys");
        int before=Directory.GetFiles("/proc/self/fd").Length;
        for(int i=0;i<64;i++){var copy=await proxy.OpenClipboardAsync(id);foreach(var fd in copy.PayloadFds)fd.Dispose();}
        Assert(Directory.GetFiles("/proc/self/fd").Length<=before+2,"repeated D-Bus restore closes server and client descriptors");
    }
}
