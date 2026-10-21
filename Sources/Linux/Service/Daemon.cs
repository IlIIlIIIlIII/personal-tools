using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tmds.DBus;
namespace PersonalTools;
public sealed record AppSettings(string Theme="system",bool Autostart=false,bool Background=true,string ClipboardShortcut="<Control><Shift>v",string MicrophoneShortcut="<Super><Alt>m");
public sealed class Daemon : IAsyncDisposable
{
    public ObjectPath ObjectPath=>new("/io/griplabs/PersonalTools");
    readonly Connection connection;readonly CancellationTokenSource lifetime=new();
    readonly ClipboardStore store;readonly ClipboardTransport transport;readonly AntigravityBridge bridge;readonly ClipboardSearch search;readonly ClipboardImageTagger tagger;
    readonly DesktopController desktop;readonly AudioClient audio=new();readonly VllmController vllm=new();readonly ShellExtensionController extension;readonly AutostartController autostart=new();
    readonly ConcurrentDictionary<string,CancellationTokenSource> searches=new();readonly ConcurrentQueue<object> events=new();readonly SemaphoreSlim commands=new(1);
    BridgeSettings bridgeSettings;AppSettings settings;string[] models=[];string? bridgeError;bool shellConnected;DateTimeOffset lastShell;
    long revision;Task? loop;Task vllmOperations=Task.CompletedTask;bool disposed;bool ownsHardware;
    object[] systemComponents=[];
    event Action<string>? changed;event Action? clipboardChanged;event Action<string>? eventSignal;
    public TaskCompletionSource Quit {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    static string SettingsPath=>Path.Combine(LinuxPaths.Config,"settings.json");
    public Daemon(Connection connection)
    {
        this.connection=connection;LinuxPaths.Ensure();
        settings=LinuxPaths.Read(SettingsPath,new AppSettings());bridgeSettings=BridgeSettings.Load();
        store=new(ClipboardSettings.Load());transport=new(store);bridge=new(BridgeSettings.LoadOrCreateApiKey());search=new(bridge,store,()=>bridgeSettings);
        tagger=new(store,(path,token)=>ClipboardImageTagger.AnalyzeAsync(bridgeSettings,path,token),message=>Log("clipboard-ai","info",message));
        desktop=new(connection);extension=new(connection);
        store.Warning+=message=>Log("clipboard","warning",message);
        store.Changed+=()=>{clipboardChanged?.Invoke();Notify();};bridge.Status+=message=>{Log("antigravity","info",message);Notify();};
    }
    public async Task InitializeAsync(bool background=true)
    {
        ownsHardware=background;await store.LoadAsync(lifetime.Token);await CollectComponentsAsync();await RefreshAsync();
        if(background&&store.Settings.AutoTagImages&&!store.Settings.Paused)foreach(var entry in store.Entries.Where(x=>x.Kind=="Image"&&x.ImageTags is null&&x.ImageTagState!="failed"))await tagger.EnqueueAsync(entry);
        if(background)loop=Task.Run(async()=>{while(!lifetime.IsCancellationRequested){try{await Task.Delay(3000,lifetime.Token);await RefreshAsync();}catch(OperationCanceledException)when(lifetime.IsCancellationRequested){break;}catch(Exception e){Log("service","error",e.Message);}}});
    }
    async Task RefreshAsync(){await Task.WhenAll(audio.RefreshAsync(lifetime.Token,ownsHardware),desktop.RefreshAsync(ownsHardware),vllm.RefreshAsync(lifetime.Token),extension.RefreshAsync(),autostart.RefreshAsync(lifetime.Token));Notify();}
    public void Log(string component,string level,string message)
    {
        // Diagnostic records never contain prompts, clipboard payloads, access tokens or CLI stderr.
        var value=new{id=Guid.NewGuid().ToString("N"),time=DateTimeOffset.UtcNow,component,level,message};events.Enqueue(value);while(events.Count>100)events.TryDequeue(out _);
        var json=JsonSerializer.Serialize(value,LinuxPaths.Json);eventSignal?.Invoke(json);
        try{var path=Path.Combine(LinuxPaths.State,"service.log");if(File.Exists(path)&&new FileInfo(path).Length>1024*1024)File.Move(path,path+".1",true);File.AppendAllText(path,JsonSerializer.Serialize(value)+"\n");File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);}catch(IOException){}
    }
    object Entry(ClipEntry entry)=>new{id=entry.Id,kind=(entry.ContentFormat??entry.Kind).ToLowerInvariant(),title=entry.Preview,preview=entry.Preview,sourceApp=entry.SourceApp,sourceAttribution=entry.SourceReliability,createdAt=entry.CreatedAt,pinned=entry.Pinned,categoryIds=store.CategoryIds(entry.Id),thumbnailUri=entry.Kind=="Image"?new Uri(store.PayloadPath(entry,entry.Attachments[0])).AbsoluteUri:null,tags=entry.ImageTags?.Tags??[],description=entry.ImageTags?.Description??"",tagState=entry.ImageTagState,attachments=entry.Attachments.Where(a=>!a.RelativePath.StartsWith("mime/",StringComparison.Ordinal)).Select(a=>new{name=a.OriginalName,mime=entry.Mimes?.FirstOrDefault(m=>m.RelativePath==a.RelativePath)?.Type??"application/octet-stream",length=a.Length}).ToArray()};
    ClipEntry Item(JsonElement value)=>store.Entries.FirstOrDefault(x=>x.Id==value.GetProperty("id").GetString())??throw new KeyNotFoundException("기록이 삭제되었습니다.");
    object Snapshot()=>new{version=1,revision=Interlocked.Read(ref revision),observedAt=DateTimeOffset.UtcNow,service=new{status="running",version="0.7.0"},
        clipboard=new{paused=store.Settings.Paused,entries=store.Entries.Take(200).Select(Entry).ToArray(),entryCount=store.Entries.Count,collections=store.Categories,settings=new{archivePath=store.Root,itemLimitBytes=store.Settings.ItemLimitBytes,totalLimitBytes=store.Settings.TotalLimitBytes,excludedApps=store.Settings.ExcludedApps,aiEnabled=store.Settings.AiSearchEnabled,taggingEnabled=store.Settings.AutoTagImages}},
        audio=audio.Snapshot,display=desktop.Snapshot,
        ai=new{antigravity=new{status=bridge.IsRunning?"running":!File.Exists(bridgeSettings.CliPath)?"unavailable":"stopped",cliPath=bridgeSettings.CliPath,model=bridgeSettings.Model,models,port=bridgeSettings.Port,timeoutSeconds=bridgeSettings.TimeoutSeconds,baseUrl=$"http://127.0.0.1:{bridgeSettings.Port}/v1",keyConfigured=true,error=bridgeError},vllm=vllm.Snapshot},
        settings=new{theme=settings.Theme,autostart=autostart.Enabled,autostartState=autostart.State,autostartError=autostart.Error,background=settings.Background,clipboardShortcut=settings.ClipboardShortcut,microphoneShortcut=settings.MicrophoneShortcut,extensionConnected=shellConnected&&DateTimeOffset.UtcNow-lastShell<TimeSpan.FromSeconds(90),extensionAvailable=extension.Available,extensionEnabled=extension.Enabled,extensionCanChange=extension.CanChange,extensionState=extension.State,extensionError=extension.Error,dataPath=LinuxPaths.Data,logPath=LinuxPaths.State},
        diagnostics=new{events=events.ToArray(),components=new[]{new{name="daemon",status="running",detail=".NET "+Environment.Version},new{name="shell",status=shellConnected?"connected":"unavailable",detail="GNOME Shell extension"}}.Cast<object>().Concat(systemComponents).ToArray()}};
    async Task CollectComponentsAsync()
    {
        var found=new List<object>();
        foreach(var (name,executable,args) in new[]{("GNOME","gnome-shell",new[]{"--version"}),("PipeWire","pipewire",new[]{"--version"}),("GTK / libadwaita","dpkg-query",new[]{"-W","-f=${Package} ${Version}\\n","libgtk-4-1","libadwaita-1-0"})})
        {try{var value=await ProcessRunner.RunAsync(executable,args,lifetime.Token,5);found.Add(new{name,status=value.ExitCode==0?"ready":"unavailable",detail=value.Output.Trim()});}catch{found.Add(new{name,status="unavailable",detail="버전을 조회할 수 없습니다."});}}
        found.Add(new{name="Linux",status="ready",detail=System.Runtime.InteropServices.RuntimeInformation.OSDescription});systemComponents=found.ToArray();
    }
    object DiagnosticExport()
    {
        // Export by allowlist. Never serialize the regular snapshot, which contains
        // previews, tags, collection names and original filenames.
        var snapshot=JsonSerializer.SerializeToElement(Snapshot(),LinuxPaths.Json);
        var audioState=snapshot.GetProperty("audio");var displayState=snapshot.GetProperty("display");
        object Status(JsonElement value)=>new{status=value.TryGetProperty("status",out var status)?status.GetString():"unknown"};
        return new{version=1,observedAt=DateTimeOffset.UtcNow,service=snapshot.GetProperty("service"),
            clipboard=new{entryCount=store.Entries.Count,paused=store.Settings.Paused,totalBytes=store.Entries.Sum(x=>x.Attachments.Sum(a=>a.Length))},
            audio=audioState,display=new{status=displayState.GetProperty("status"),monitors=displayState.GetProperty("monitors"),observedAt=displayState.GetProperty("observedAt")},
            ai=new{antigravity=Status(snapshot.GetProperty("ai").GetProperty("antigravity")),vllm=Status(snapshot.GetProperty("ai").GetProperty("vllm"))},
            components=snapshot.GetProperty("diagnostics").GetProperty("components"),
            events=snapshot.GetProperty("diagnostics").GetProperty("events").EnumerateArray().Select(e=>new{time=e.GetProperty("time"),component=e.GetProperty("component"),level=e.GetProperty("level")}).ToArray()};
    }
    public Task<string> GetSnapshotAsync()=>Task.FromResult(Serialize(Snapshot()));
    void Notify(){Interlocked.Increment(ref revision);try{changed?.Invoke(Serialize(Snapshot()));}catch(Exception e)when(e is IOException or ArgumentException){Console.Error.WriteLine(e.Message);}}
    static string Serialize(object value){string json=JsonSerializer.Serialize(value,LinuxPaths.Json);if(Encoding.UTF8.GetByteCount(json)>1024*1024)throw new IOException("Response exceeds 1 MiB. Narrow the clipboard query.");return json;}
    static string Ok(object? data=null,string? operationId=null)=>Serialize(new{ok=true,data,operationId});
    static string Error(Exception e)=>Serialize(new{ok=false,error=new{code=e is OperationCanceledException?"cancelled":e is ArgumentException or JsonException?"invalid_argument":e is KeyNotFoundException?"not_found":e is NotSupportedException?"unavailable":"operation_failed",message=e.Message,retryable=e is IOException}});
    static JsonElement Parse(string payload){if(Encoding.UTF8.GetByteCount(payload)>1024*1024)throw new ArgumentException("Payload exceeds 1 MiB.");using var doc=JsonDocument.Parse(payload);if(doc.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Payload must be a JSON object.");return doc.RootElement.Clone();}
    public async Task<string> CaptureClipboardAsync(string metadata,SafeFileHandle[] payloadFds)
    {
        try{var entry=await transport.CaptureAsync(metadata,payloadFds,lifetime.Token);if(entry is not null&&store.Settings.AutoTagImages)await tagger.EnqueueAsync(entry);return Ok(new{entry=entry is null?null:Entry(entry)});}catch(Exception e){Log("clipboard","warning","클립보드 캡처가 거부되었습니다.");return Error(e);}finally{foreach(var fd in payloadFds)fd.Dispose();}
    }
    public Task<(string Metadata,SafeFileHandle[] PayloadFds)> OpenClipboardAsync(string entryId)=>Task.FromResult(transport.Open(entryId));
    public async Task<string> ExecuteAsync(string domain,string action,string payload)
    {
        try {
            var value=Parse(payload);object? result=null;string? operation=null;
            // Long-running searches must not prevent cancellation or normal desktop controls.
            if(domain=="clipboard"&&action=="ai-search")return await AiSearchAsync(value);
            if(domain=="clipboard"&&action=="cancel-search"){if(searches.TryGetValue(value.GetProperty("requestId").GetString()!,out var searchToken))searchToken.Cancel();return Ok();}
            await commands.WaitAsync(lifetime.Token);
            try{
                switch(domain){
                    case "clipboard": result=await ClipboardAsync(action,value);break;
                    case "audio":
                        if(action=="route-microphone"){await audio.RouteMicrophoneAsync(value.GetProperty("enabled").GetBoolean(),lifetime.Token);await audio.RefreshAsync(lifetime.Token);break;}
                        if(action is "configure")value=audio.Configuration(value);
                        result=await audio.SendAsync(action=="refresh"?"status":action,value,lifetime.Token);
                        if(action is "configure" or "import-eq" or "mute")await audio.PersistAsync(action,value,lifetime.Token);
                        if(action=="configure"&&value.TryGetProperty("eqEnabled",out var eq)&&eq.GetBoolean())await audio.UpdateRoutesAsync(explicitOutput:true,token:lifetime.Token);
                        await audio.RefreshAsync(lifetime.Token);break;
                    case "display":await desktop.ExecuteAsync(action,value);break;
                    case "antigravity":result=await BridgeAsync(action,value);break;
                    case "vllm":
                        switch(action){case "refresh":await vllm.RefreshAsync(lifetime.Token);break;case "configure":await vllm.ConfigureAsync(value,lifetime.Token);break;case "logs":result=new{text=vllm.Logs()};break;
                            case "start":case "stop":operation=Guid.NewGuid().ToString("N");vllmOperations=RunVllmOperationAsync(vllmOperations,action,operation);break;
                            default:throw new ArgumentException("Unknown vLLM action.");}break;
                    case "settings":if(action!="update")throw new ArgumentException("Unknown settings action.");await UpdateSettingsAsync(value);break;
                    case "shell":if(action!="status")throw new ArgumentException("Unknown Shell action.");shellConnected=value.GetProperty("connected").GetBoolean();lastShell=DateTimeOffset.UtcNow;break;
                    case "diagnostics":switch(action){case "refresh":await RefreshAsync();break;case "audio":result=await audio.SendAsync("status",value,lifetime.Token);break;case "export":var path=Path.Combine(LinuxPaths.State,$"diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");LinuxPaths.Write(path,DiagnosticExport());result=new{path};break;default:throw new ArgumentException("Unknown diagnostics action.");}break;
                    // The transport completes Quit only after sending this response.
                    case "lifecycle":if(action!="quit")throw new ArgumentException("Unknown lifecycle action.");break;
                    default:throw new ArgumentException("Unknown command domain.");
                }
            }finally{commands.Release();}
            // A client may query again when Changed arrives. Broadcasting an
            // unchanged snapshot for a read causes an unbounded query/Changed loop.
            bool readOnly=(domain,action) is ("clipboard","query") or ("clipboard","detail") or ("vllm","logs") or ("antigravity","api-key") or ("diagnostics","audio") or ("diagnostics","export");
            if(!readOnly)Notify();return Ok(result,operation);
        }catch(Exception e){return Error(e);}
    }
    async Task RunVllmOperationAsync(Task previous,string action,string operation)
    {
        // Enqueued while commands is held: Start followed by Stop must remain in
        // that order even when the thread pool schedules continuations differently.
        await previous;
        try{
            lifetime.Token.ThrowIfCancellationRequested();
            if(action=="start")await vllm.StartAsync(lifetime.Token);else await vllm.StopAsync(lifetime.Token);
            Log("vllm","info",$"작업 {operation}: {action} 요청 전달됨");
        }catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception e){Log("vllm","error",e.Message);}
        if(!lifetime.IsCancellationRequested)Notify();
    }
    async Task<object?> ClipboardAsync(string action,JsonElement value)
    {
        switch(action){
            case "query":
                var filter=value.TryGetProperty("filter",out var f)?f:Parse("{}");
                string[] Array(string key)=>filter.TryGetProperty(key,out var values)?values.EnumerateArray().Select(x=>x.GetString()!).ToArray():[];
                DateOnly? Date(string key)=>filter.TryGetProperty(key,out var date)&&date.ValueKind==JsonValueKind.String?DateOnly.Parse(date.GetString()!,System.Globalization.CultureInfo.InvariantCulture):null;
                var query=await store.FindAsync(value.TryGetProperty("query",out var q)?q.GetString()??"":"",new ClipboardFilter{Processes=Array("apps"),Formats=Array("formats"),From=Date("from"),Through=Date("through"),CategoryId=filter.TryGetProperty("categoryId",out var category)?category.GetString():null,PinnedOnly=filter.TryGetProperty("pinnedOnly",out var pins)&&pins.GetBoolean()},lifetime.Token);
                int offset=value.TryGetProperty("offset",out var offsetValue)?offsetValue.GetInt32():0;
                int limit=value.TryGetProperty("limit",out var limitValue)?limitValue.GetInt32():60;
                if(offset<0||limit<1||limit>500)throw new ArgumentException("Query offset must be nonnegative and limit must be 1–500.");
                return new{entries=query.Skip(offset).Take(limit).Select(Entry).ToArray(),total=query.Length};
            case "pause":await store.UpdateSettingsAsync(store.Settings with{Paused=value.GetProperty("paused").GetBoolean()});break;
            case "pin":await store.SetPinnedAsync(Item(value),value.GetProperty("pinned").GetBoolean());break;
            case "delete":await store.DeleteAsync(Item(value));break;
            case "save-collection":return await store.SaveCategoryAsync(value.GetProperty("name").GetString()!,value.TryGetProperty("id",out var id)?id.GetString():null);
            case "delete-collection":await store.DeleteCategoryAsync(value.GetProperty("id").GetString()!);break;
            case "set-collections":await store.SetCategoriesAsync(Item(value).Id,value.GetProperty("categoryIds").EnumerateArray().Select(x=>x.GetString()!));break;
            case "settings":
                var next=store.Settings with{ArchivePath=value.TryGetProperty("archivePath",out var path)?path.GetString()!:store.Root,ItemLimitBytes=value.TryGetProperty("itemLimitBytes",out var itemLimit)?itemLimit.GetInt64():store.Settings.ItemLimitBytes,TotalLimitBytes=value.TryGetProperty("totalLimitBytes",out var totalLimit)?totalLimit.GetInt64():store.Settings.TotalLimitBytes,ExcludedApps=value.TryGetProperty("excludedApps",out var apps)?apps.EnumerateArray().Select(x=>x.GetString()!).ToArray():store.Settings.ExcludedApps,AiSearchEnabled=value.TryGetProperty("aiEnabled",out var ai)?ai.GetBoolean():store.Settings.AiSearchEnabled,AutoTagImages=value.TryGetProperty("taggingEnabled",out var tag)?tag.GetBoolean():store.Settings.AutoTagImages};
                if(!Path.IsPathFullyQualified(next.ArchivePath)||next.ItemLimitBytes>512L*1024*1024||next.ExcludedApps.Any(x=>x is null||x.Length>512))throw new ArgumentException("저장 경로, 용량(최대 512 MiB) 및 제외 앱을 확인해 주세요.");
                await store.UpdateSettingsAsync(next);if(next.AutoTagImages)foreach(var entry in store.Entries.Where(x=>x.Kind=="Image"&&x.ImageTags is null&&x.ImageTagState!="failed"))await tagger.EnqueueAsync(entry);break;
            case "tag":if(!store.Settings.AutoTagImages)throw new InvalidOperationException("이미지 AI 태깅을 먼저 활성화해 주세요.");var image=Item(value);if(image.Kind!="Image")throw new ArgumentException("이미지 항목만 태깅할 수 있습니다.");await tagger.EnqueueAsync(image);return new{queued=true};
            case "detail":
                var entryDetail=Item(value);string? text=null,textPath=null;bool textTruncated=false;long textBytes=0;
                if(entryDetail.Kind=="Text"){
                    textPath=store.PayloadPath(entryDetail,entryDetail.Attachments[0]);textBytes=new FileInfo(textPath).Length;
                    using var reader=new StreamReader(textPath,Encoding.UTF8,true);
                    var buffer=new char[64*1024+1];int count=await reader.ReadBlockAsync(buffer.AsMemory(),lifetime.Token);
                    textTruncated=count>64*1024;text=new string(buffer,0,Math.Min(count,64*1024));
                }
                return new{entry=Entry(entryDetail),text,textPath,textBytes,textTruncated,files=entryDetail.Attachments.Where(a=>!a.RelativePath.StartsWith("mime/")).Select(a=>new{path=store.PayloadPath(entryDetail,a),name=a.OriginalName,length=a.Length}).ToArray(),imageUri=entryDetail.Kind=="Image"?new Uri(store.PayloadPath(entryDetail,entryDetail.Attachments[0])).AbsoluteUri:null};
            default:throw new ArgumentException("Unknown clipboard action.");
        }
        return null;
    }
    async Task<string> AiSearchAsync(JsonElement value)
    {
        if(!store.Settings.AiSearchEnabled)return Error(new InvalidOperationException("클립보드 AI 검색을 먼저 활성화해 주세요."));
        string requestId=value.GetProperty("requestId").GetString()!;if(string.IsNullOrWhiteSpace(requestId))return Error(new ArgumentException("requestId is required."));
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if(!searches.TryAdd(requestId,cancel))return Error(new ArgumentException("requestId already exists."));
        try{string? category=value.TryGetProperty("categoryId",out var c)?c.GetString():null;var result=await search.SearchClipboardAsync(value.GetProperty("query").GetString()!,category is null?null:store.Entries.Where(e=>store.InCategory(e,category)).Select(e=>e.Id).ToArray(),cancel.Token);return Ok(new{entries=result.ItemIds.Select(id=>store.Entries.FirstOrDefault(x=>x.Id==id)).Where(x=>x is not null).Select(x=>Entry(x!)).ToArray(),summary=result.Answer,limitations=result.Limitations});}
        catch(Exception e){return Error(e);}finally{searches.TryRemove(requestId,out _);}
    }
    async Task<object?> BridgeAsync(string action,JsonElement value)
    {
        try {
            switch(action){
                case "configure":
                    if(bridge.IsRunning)throw new InvalidOperationException("브릿지를 중지한 뒤 설정을 바꿔 주세요.");
                    var next=bridgeSettings with{CliPath=value.TryGetProperty("cliPath",out var path)?path.GetString()!:bridgeSettings.CliPath,Model=value.TryGetProperty("model",out var model)?model.GetString()!:bridgeSettings.Model,Port=value.TryGetProperty("port",out var port)?port.GetInt32():bridgeSettings.Port,TimeoutSeconds=value.TryGetProperty("timeoutSeconds",out var timeout)?timeout.GetInt32():bridgeSettings.TimeoutSeconds};
                    next.Validate();next.Save();bridgeSettings=next;break;
                case "models":models=await AntigravityBridge.GetModelsAsync(bridgeSettings,lifetime.Token);return new{models};
                case "start":await bridge.StartAsync(bridgeSettings,lifetime.Token);break;
                case "stop":await bridge.StopAsync();break;
                case "api-key":return new{apiKey=bridge.ApiKey};
                case "login":
                    bridgeSettings.Validate();var terminal=new ProcessStartInfo("x-terminal-emulator"){UseShellExecute=false};terminal.ArgumentList.Add("-e");terminal.ArgumentList.Add(bridgeSettings.CliPath);Process.Start(terminal)?.Dispose();return new{opened=true};
                case "test":
                    bridgeSettings.Validate();using(var timeoutToken=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)){timeoutToken.CancelAfter(TimeSpan.FromSeconds(bridgeSettings.TimeoutSeconds));var reply=await AntigravityCli.RunAsync(bridgeSettings,new(bridgeSettings.Model,value.GetProperty("prompt").GetString()!,false,false),_=>Task.CompletedTask,timeoutToken.Token);return new{response=reply.Content};}
                default:throw new ArgumentException("Unknown Antigravity action.");
            }
            bridgeError=null;return null;
        }catch(Exception e){bridgeError=e.Message;throw;}
    }
    async Task UpdateSettingsAsync(JsonElement value)
    {
        var next=settings with{Theme=value.TryGetProperty("theme",out var theme)?theme.GetString()!:settings.Theme,Autostart=value.TryGetProperty("autostart",out var autostart)?autostart.GetBoolean():settings.Autostart,Background=value.TryGetProperty("background",out var background)?background.GetBoolean():settings.Background,ClipboardShortcut=value.TryGetProperty("clipboardShortcut",out var clip)?clip.GetString()!:settings.ClipboardShortcut,MicrophoneShortcut=value.TryGetProperty("microphoneShortcut",out var mic)?mic.GetString()!:settings.MicrophoneShortcut};
        next=next with{ClipboardShortcut=ShortcutConfiguration.Canonical(next.ClipboardShortcut),MicrophoneShortcut=ShortcutConfiguration.Canonical(next.MicrophoneShortcut)};
        if(next.Theme is not("system" or "light" or "dark")||next.ClipboardShortcut==next.MicrophoneShortcut)throw new ArgumentException("테마 또는 단축키 설정이 올바르지 않습니다.");
        bool shortcutsChanged=next.ClipboardShortcut!=settings.ClipboardShortcut||next.MicrophoneShortcut!=settings.MicrophoneShortcut;
        IPersonalToolsShell? shell=shortcutsChanged?connection.CreateProxy<IPersonalToolsShell>("io.griplabs.PersonalTools.Shell","/io/griplabs/PersonalTools/Shell"):null;
        async Task ConfigureShortcuts(AppSettings target)
        {
            var result=Parse(await shell!.ConfigureShortcutsAsync(Serialize(new{clipboardShortcut=target.ClipboardShortcut,microphoneShortcut=target.MicrophoneShortcut})));
            if(!result.GetProperty("ok").GetBoolean())throw new IOException(result.TryGetProperty("error",out var error)?error.ToString():"단축키 등록에 실패했습니다.");
        }
        // Shell only registers keys; this service remains the settings owner.
        // ConfigureShortcuts must never call settings/update back into this gate.
        if(shortcutsChanged)await ConfigureShortcuts(next);
        try
        {
            if(value.TryGetProperty("autostart",out var autostartValue))await this.autostart.ConfigureAsync(autostartValue.GetBoolean(),lifetime.Token);
            if(value.TryGetProperty("extensionEnabled",out var extensionEnabled)){await extension.ConfigureAsync(extensionEnabled.GetBoolean());if(!extensionEnabled.GetBoolean())shellConnected=false;}
            next=next with{Autostart=this.autostart.Enabled};LinuxPaths.Write(SettingsPath,next);settings=next;
        }
        catch(Exception saveError)
        {
            if(shortcutsChanged)try{await ConfigureShortcuts(settings);}catch(Exception restoreError){throw new IOException("설정을 저장하지 못했고 이전 단축키 복원도 실패했습니다. "+restoreError.Message,saveError);}
            throw;
        }
    }
    public Task<IDisposable> WatchChangedAsync(Action<string> handler){changed+=handler;return Task.FromResult<IDisposable>(new Subscription(()=>changed-=handler));}
    public Task<IDisposable> WatchClipboardChangedAsync(Action handler){clipboardChanged+=handler;return Task.FromResult<IDisposable>(new Subscription(()=>clipboardChanged-=handler));}
    public Task<IDisposable> WatchEventAsync(Action<string> handler){eventSignal+=handler;return Task.FromResult<IDisposable>(new Subscription(()=>eventSignal-=handler));}
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;disposed=true;lifetime.Cancel();foreach(var search in searches.Values)search.Cancel();if(loop is not null)try{await loop;}catch(OperationCanceledException){}
        await vllmOperations;
        await tagger.DisposeAsync();await bridge.DisposeAsync();if(!ownsHardware){lifetime.Dispose();return;}try{await desktop.ShutdownAsync();}catch(Exception e){Log("display","error","HDR 복원 대기: "+e.Message);}
        try{await audio.RestoreRoutesAsync();}catch(Exception e){Log("audio","warning","기본 장치 복원 대기: "+e.Message);}
        try{await audio.SendAsync("shutdown",Parse("{}"));}catch(Exception e){Log("audio","warning",e.Message);}lifetime.Dispose();
    }
}
