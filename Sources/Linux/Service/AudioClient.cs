using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace PersonalTools;
public sealed class AudioClient
{
    bool restored;
    bool startupMuteReset;
    DateTime socketCreated;
    readonly SemaphoreSlim refreshGate=new(1);
    readonly AudioRouting routing;
    public AudioClient(AudioRouting? routing=null){this.routing=routing??new AudioRouting();}
    static string SettingsPath=>Path.Combine(LinuxPaths.Config,"audio.json");
    static string SocketPath=>Path.Combine(LinuxPaths.Runtime,"audio.sock");
    static readonly JsonSerializerOptions WireJson=new(JsonSerializerDefaults.Web);
    public JsonElement Configuration(JsonElement args)
    {
        var values=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(args.GetRawText())!;
        if(values.TryGetValue("aecEnabled",out var enabled)&&enabled.GetBoolean())
        {
            string runtime=Environment.GetEnvironmentVariable("PERSONALTOOLS_NPU_CONFIG")??Path.Combine(LinuxPaths.Data,"npu/runtime.json");
            if(!File.Exists(runtime))throw new IOException("NPU 런타임과 모델이 설치되지 않았습니다. NPU 준비 도구를 먼저 실행해 주세요.");
            var config=LinuxPaths.Read(runtime,new Dictionary<string,JsonElement>());
            foreach(var key in new[]{"aecBackend","npuModelDirectory","npuRuntimeLibrary"})
            {if(!config.TryGetValue(key,out var value))throw new IOException("NPU 런타임 설정이 올바르지 않습니다.");values[key]=value;}
        }
        return JsonSerializer.SerializeToElement(values,WireJson);
    }
    public async Task PersistAsync(string command, JsonElement args, CancellationToken token=default)
    {
        var values=LinuxPaths.Read(SettingsPath,new Dictionary<string,JsonElement>());
        foreach(var property in args.EnumerateObject()) if(property.Name is "outputId" or "inputId" or "eqEnabled" or "aecEnabled" or "leftFilters" or "rightFilters" or "aecBackend" or "npuModelDirectory" or "npuRuntimeLibrary" or "muted")values[property.Name]=property.Value.Clone();
        if(command=="import-eq"){
            var path=args.GetProperty("path").GetString()!;string channel=args.GetProperty("channel").GetString()!;
            if(channel is not("left" or "right"))throw new ArgumentException("Unknown EQ channel.");
            string directory=Path.Combine(LinuxPaths.Data,"filters");Directory.CreateDirectory(directory);string target=Path.Combine(directory,channel+".txt");
            File.Copy(path,target,true);File.SetUnixFileMode(target,UnixFileMode.UserRead|UnixFileMode.UserWrite);values[channel+"File"]=JsonSerializer.SerializeToElement(target);
        }
        // Persist only fields changed by this command. A reconnecting engine can
        // report empty filters before restoration; mute must never erase saved EQ.
        var actual=await SendAsync("status",JsonSerializer.SerializeToElement(new{}),token);
        IEnumerable<string> changed=command switch {
            "mute"=>new[]{"muted"},
            "import-eq"=>new[]{args.GetProperty("channel").GetString()+"Filters"},
            "configure"=>new[]{"leftFilters","rightFilters"}.Where(key=>args.TryGetProperty(key,out _)),
            _=>Array.Empty<string>()
        };
        foreach(var key in changed)if(actual.TryGetProperty(key,out var value))values[key]=value.Clone();
        LinuxPaths.Write(SettingsPath,values);
    }
    public object Snapshot { get; private set; } = Unavailable("오디오 엔진이 연결되지 않았습니다.");
    static bool Flag(JsonElement value,string key)=>value.TryGetProperty(key,out var flag)&&flag.ValueKind==JsonValueKind.True;
    static bool DeviceAvailable(JsonElement actual,string side)
    {
        if(!actual.TryGetProperty(side+"Id",out var id)||string.IsNullOrEmpty(id.GetString())||!actual.TryGetProperty(side+"s",out var devices))return false;
        return devices.EnumerateArray().Any(device=>device.GetProperty("id").GetString()==id.GetString());
    }
    public async Task UpdateRoutesAsync(bool explicitOutput=false,bool explicitMicrophone=false,CancellationToken token=default)
    {
        var actual=await SendAsync("status",JsonSerializer.SerializeToElement(new{}),token);
        var saved=LinuxPaths.Read(SettingsPath,new Dictionary<string,JsonElement>());
        bool microphone=saved.TryGetValue("processedMicrophoneDefault",out var wanted)&&wanted.GetBoolean();
        await routing.RefreshAsync(Flag(actual,"eqEnabled"),microphone,DeviceAvailable(actual,"output"),DeviceAvailable(actual,"input"),explicitOutput:explicitOutput,explicitMicrophone:explicitMicrophone,token:token);
    }
    public async Task RouteMicrophoneAsync(bool enabled,CancellationToken token=default)
    {
        var actual=await SendAsync("status",JsonSerializer.SerializeToElement(new{}),token);
        await routing.RefreshAsync(Flag(actual,"eqEnabled"),enabled,DeviceAvailable(actual,"output"),DeviceAvailable(actual,"input"),explicitMicrophone:true,token:token);
        var saved=LinuxPaths.Read(SettingsPath,new Dictionary<string,JsonElement>());
        saved["processedMicrophoneDefault"]=JsonSerializer.SerializeToElement(enabled);LinuxPaths.Write(SettingsPath,saved);
    }
    public Task RestoreRoutesAsync()=>routing.ShutdownAsync();
    static object Unavailable(string error,Dictionary<string,JsonElement>? saved=null)
    {
        // Filters are persisted configuration, not evidence that processing is
        // active. Keep them visible through engine startup/restart failures.
        var state=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(new {
            status="unavailable", error, outputs=Array.Empty<object>(), inputs=Array.Empty<object>(), outputId="", inputId="",
            eqEnabled=false, aecEnabled=false, muted=false, leftFilters=Array.Empty<object>(), rightFilters=Array.Empty<object>(),
            leftFile="",rightFile="", npu=new {status="unavailable",device="",inferences=0},latencyMs=(double?)null,inputLevelDb=(double?)null
        },WireJson))!;
        if(saved is not null)foreach(var key in new[]{"leftFilters","rightFilters","leftFile","rightFile"})
            if(saved.TryGetValue(key,out var value))state[key]=value.Clone();
        return state;
    }
    static string ConnectionError(SocketException error)=>error.SocketErrorCode==SocketError.AccessDenied
        ?"오디오 엔진 연결 권한이 없습니다. 진단에서 사용자 서비스를 확인해 주세요."
        :"오디오 엔진에 연결할 수 없습니다. 사용자 서비스가 시작되면 자동으로 다시 연결합니다.";
    static async Task<Socket> ConnectAsync(CancellationToken token)
    {
        // Type=simple only guarantees that systemd spawned the engine. Its Unix
        // socket can still be absent during login or an engine restart. Retry
        // only before sending: replaying a written toggle/import is unsafe.
        var watch=Stopwatch.StartNew();
        while(true)
        {
            var socket=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
            try{await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath),token);return socket;}
            catch(SocketException error)
            {
                socket.Dispose();
                if(error.SocketErrorCode is not(SocketError.AddressNotAvailable or SocketError.ConnectionRefused or SocketError.TryAgain)
                    ||watch.Elapsed>=TimeSpan.FromSeconds(2))throw new IOException(ConnectionError(error),error);
                await Task.Delay(100,token);
            }
            catch{socket.Dispose();throw;}
        }
    }
    public async Task<JsonElement> SendAsync(string command, JsonElement args, CancellationToken token=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket=await ConnectAsync(timeout.Token);
        await using var stream=new NetworkStream(socket,false);
        string id=Guid.NewGuid().ToString("N");
        var request=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id,command,args},WireJson)+"\n");
        if(request.Length>1024*1024)throw new ArgumentException("Audio request too large.");
        await stream.WriteAsync(request,timeout.Token); await stream.FlushAsync(timeout.Token);
        using var reader=new StreamReader(stream,Encoding.UTF8);
        var response=new StringBuilder(); var character=new char[1];
        while(await reader.ReadAsync(character,timeout.Token)>0) {if(character[0]=='\n')break;response.Append(character[0]);if(response.Length>1024*1024)throw new IOException("Audio response too large.");}
        if(response.Length==0)throw new IOException("오디오 엔진 연결이 끊어졌습니다. 상태를 다시 확인한 뒤 재시도해 주세요.");
        using var json=JsonDocument.Parse(response.ToString());
        var root=json.RootElement;
        if(root.GetProperty("id").GetString()!=id)throw new IOException("Audio response ID mismatch.");
        if(!root.GetProperty("ok").GetBoolean())throw new IOException(root.TryGetProperty("error",out var error)?error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("message",out var message)?message.GetString():error.ToString():"오디오 엔진 명령을 실행하지 못했습니다.");
        return root.TryGetProperty("data",out var data)?data.Clone():JsonSerializer.SerializeToElement(new{});
    }
    public async Task RefreshAsync(CancellationToken token=default,bool restore=true)
    {
        await refreshGate.WaitAsync(token);
        Dictionary<string,JsonElement>? saved=null;
        try {
            var created=File.GetLastWriteTimeUtc(SocketPath);
            if(created!=socketCreated){restored=false;socketCreated=created;}
            saved=LinuxPaths.Read(SettingsPath,new Dictionary<string,JsonElement>());
            bool resetMute=restore&&!startupMuteReset;
            if(resetMute){
                // A new app session always starts with an open microphone. Keep
                // persisting later user changes for engine recovery in this session.
                await SendAsync("mute",JsonSerializer.SerializeToElement(new{muted=false}),token);
                saved["muted"]=JsonSerializer.SerializeToElement(false);
                LinuxPaths.Write(SettingsPath,saved);
                startupMuteReset=true;
            }
            if(restore&&!restored&&saved.Count>0){
                var values=new Dictionary<string,JsonElement>(saved);values.Remove("leftFile");values.Remove("rightFile");values.Remove("muted");values.Remove("processedMicrophoneDefault");
                await SendAsync("restore",Configuration(JsonSerializer.SerializeToElement(values)),token);
                if(!resetMute&&saved.TryGetValue("muted",out var muted))await SendAsync("mute",JsonSerializer.SerializeToElement(new{muted=muted.GetBoolean()}),token);
            }
            var actual=await SendAsync("status",JsonSerializer.SerializeToElement(new{}),token);
            var snapshot=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(actual.GetRawText())!;
            foreach(var key in new[]{"leftFile","rightFile"})if(saved.TryGetValue(key,out var value))snapshot[key]=value;
            bool microphone=saved.TryGetValue("processedMicrophoneDefault",out var wanted)&&wanted.GetBoolean();
            await routing.RefreshAsync(Flag(actual,"eqEnabled"),microphone,DeviceAvailable(actual,"output"),DeviceAvailable(actual,"input"),mutate:restore,token:token);
            snapshot["routing"]=JsonSerializer.SerializeToElement(routing.Snapshot,WireJson);
            Snapshot=snapshot;if(restore){restored=true;socketCreated=File.GetLastWriteTimeUtc(SocketPath);}
        }catch(Exception e){restored=false;Snapshot=Unavailable(e.Message,saved);if(restore)try{await routing.ShutdownAsync(token);}catch(IOException){}catch(OperationCanceledException){}}
        finally{refreshGate.Release();}
    }
}
