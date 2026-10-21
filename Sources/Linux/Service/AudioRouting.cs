using System.Text.Json;
namespace PersonalTools;

public sealed record AudioRouteObservation(Dictionary<string,string?> Effective,Dictionary<string,string?> Configured,HashSet<string> Nodes,HashSet<string>? Bluetooth=null);
public interface IAudioRouteBackend
{
    Task<AudioRouteObservation> ReadAsync(CancellationToken token);
    Task SetConfiguredAsync(string kind,string? name,CancellationToken token);
}
public sealed class PipeWireRouteBackend : IAudioRouteBackend
{
    static bool Flag(JsonElement props,string key)=>props.TryGetProperty(key,out var value)&&(value.ValueKind==JsonValueKind.True||(value.ValueKind==JsonValueKind.String&&value.GetString()=="true"));
    static string? Text(JsonElement props,string key)=>props.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    // WirePlumber exposes headphones through a loopback sink, or without one
    // through the device node itself; its internal nodes are not endpoints.
    static bool Bluetooth(JsonElement props)=>Text(props,"media.class")=="Audio/Sink"&&!Flag(props,"api.bluez5.internal")&&(Flag(props,"bluez5.sink-loopback")||Text(props,"device.api")=="bluez5");
    public async Task<AudioRouteObservation> ReadAsync(CancellationToken token)
    {
        var result=await ProcessRunner.RunAsync("pw-dump",[],token,5);
        if(result.ExitCode!=0)throw new IOException("PipeWire 기본 장치를 조회할 수 없습니다.");
        using var document=JsonDocument.Parse(result.Output);
        var effective=new Dictionary<string,string?>();var configured=new Dictionary<string,string?>();var nodes=new HashSet<string>();var bluetooth=new HashSet<string>();
        foreach(var item in document.RootElement.EnumerateArray())
        {
            if(item.GetProperty("type").GetString()=="PipeWire:Interface:Node"&&item.TryGetProperty("info",out var info)&&info.TryGetProperty("props",out var props)&&props.TryGetProperty("node.name",out var node)){nodes.Add(node.GetString()!);if(Bluetooth(props))bluetooth.Add(node.GetString()!);}
            if(item.GetProperty("type").GetString()!="PipeWire:Interface:Metadata"||!item.TryGetProperty("props",out var metadataProps)||!metadataProps.TryGetProperty("metadata.name",out var metadataName)||metadataName.GetString()!="default"||!item.TryGetProperty("metadata",out var entries))continue;
            foreach(var entry in entries.EnumerateArray())
            {
                if(entry.GetProperty("subject").GetInt32()!=0)continue;
                var key=entry.GetProperty("key").GetString();var value=entry.GetProperty("value");
                if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty("name",out var name))continue;
                foreach(var (kind,suffix) in new[]{("output","sink"),("microphone","source")})
                {if(key==$"default.audio.{suffix}")effective[kind]=name.GetString();if(key==$"default.configured.audio.{suffix}")configured[kind]=name.GetString();}
            }
        }
        return new(effective,configured,nodes,bluetooth);
    }
    public async Task SetConfiguredAsync(string kind,string? name,CancellationToken token)
    {
        string key=kind switch{"output"=>"default.configured.audio.sink","microphone"=>"default.configured.audio.source",_=>throw new ArgumentException("Unknown audio route kind.")};
        string[] args=name is null?["-n","default","-d","0",key]:["-n","default","0",key,JsonSerializer.Serialize(new{name}),"Spa:String:JSON"];
        var result=await ProcessRunner.RunAsync("pw-metadata",args,token,5);
        if(result.ExitCode!=0)throw new IOException("PipeWire 기본 장치를 변경할 수 없습니다.");
    }
}

// The daemon owns route intent and its journal. The RT engine never changes a
// system default. A manual desktop change releases ownership until explicit reuse.
public sealed class AudioRouting
{
    public sealed record Ownership(string Target,string? PreviousConfigured,string? PreviousEffective,string Phase="owned");
    readonly IAudioRouteBackend backend;readonly string journalPath;readonly SemaphoreSlim gate=new(1);
    Dictionary<string,Ownership> journal;HashSet<string>? bluetooth;
    public object Snapshot {get;private set;}=new{output=new{state="unknown",enabled=false},microphone=new{state="unknown",enabled=false}};
    public AudioRouting(IAudioRouteBackend? backend=null,string? journalPath=null)
    {
        this.backend=backend??new PipeWireRouteBackend();this.journalPath=journalPath??Path.Combine(LinuxPaths.State,"audio-routes.json");
        journal=LinuxPaths.Read(this.journalPath,new Dictionary<string,Ownership>());
    }
    void Save()=>LinuxPaths.Write(journalPath,journal);
    static string Target(string kind)=>kind=="output"?"personal-tools-output":"personal-tools-mic";
    static string? Value(Dictionary<string,string?> values,string key)=>values.GetValueOrDefault(key);
    async Task<AudioRouteObservation> ObserveUntilAsync(string kind,string? configured,string? effective,CancellationToken token)
    {
        AudioRouteObservation? observation=null;
        for(int i=0;i<30;i++)
        {
            observation=await backend.ReadAsync(token);
            if(Value(observation.Configured,kind)==configured&&(effective is null||Value(observation.Effective,kind)==effective))return observation;
            await Task.Delay(50,token);
        }
        throw new IOException("기본 오디오 장치 변경을 확인할 수 없습니다.");
    }
    async Task<AudioRouteObservation> ReconcileAsync(string kind,bool wanted,bool explicitRequest,bool available,AudioRouteObservation observation,CancellationToken token)
    {
        string target=Target(kind);journal.TryGetValue(kind,out var previous);
        if(previous?.Phase=="pending")
        {
            if(Value(observation.Configured,kind)==target){previous=previous with{Phase="owned"};journal[kind]=previous;Save();}
            else if(Value(observation.Configured,kind)==previous.PreviousConfigured){journal.Remove(kind);Save();previous=null;}
            else{previous=previous with{Phase="overridden"};journal[kind]=previous;Save();}
        }
        if(previous is not null&&previous.Phase=="owned"&&Value(observation.Configured,kind)!=target)
        {previous=previous with{Phase="overridden"};journal[kind]=previous;Save();}
        if(previous?.Phase=="suspended"&&Value(observation.Configured,kind)!=previous.PreviousConfigured)
        {previous=previous with{Phase="overridden"};journal[kind]=previous;Save();}
        if(!wanted)
        {
            if(previous?.Phase=="owned")
            {
                await backend.SetConfiguredAsync(kind,previous.PreviousConfigured,token);
                var expected=previous.PreviousConfigured??previous.PreviousEffective;
                observation=await ObserveUntilAsync(kind,previous.PreviousConfigured,expected is not null&&observation.Nodes.Contains(expected)?expected:null,token);
            }
            if(previous is not null){journal.Remove(kind);Save();}
            return observation;
        }
        if(previous?.Phase=="overridden"&&!explicitRequest)return observation;
        if(!available||!observation.Nodes.Contains(target))
        {
            // Do not leave a disconnected processor as the desktop default.
            // Remember the suspended ownership so a later manual choice wins
            // over automatic reacquisition when the physical device returns.
            if(previous?.Phase=="owned")
            {
                await backend.SetConfiguredAsync(kind,previous.PreviousConfigured,token);
                observation=await ObserveUntilAsync(kind,previous.PreviousConfigured,null,token);
                previous=previous with{Phase="suspended"};journal[kind]=previous;Save();
            }
            throw new IOException(kind=="output"?"EQ 출력에 연결할 물리 장치를 먼저 선택해 주세요.":"처리된 마이크에 연결할 물리 입력을 먼저 선택해 주세요.");
        }
        if(previous?.Phase=="owned")return observation;
        // If another app/user already selected our endpoint, we may use it but
        // have no prior route to restore and must not manufacture ownership.
        if(Value(observation.Configured,kind)==target&&Value(observation.Effective,kind)==target)return observation;
        journal[kind]=previous?.Phase=="suspended"?previous with{Phase="pending"}:new(target,Value(observation.Configured,kind),Value(observation.Effective,kind),"pending");Save();
        await backend.SetConfiguredAsync(kind,target,token);
        observation=await ObserveUntilAsync(kind,target,target,token);
        journal[kind]=journal[kind] with{Phase="owned"};Save();return observation;
    }
    // Headphones are expected to take over the output when they connect, but
    // WirePlumber never leaves a present configured default, and the processed
    // output is one whenever its device is. Switch once per arrival and treat it
    // as a manual choice: no automatic reacquisition until EQ is chosen again.
    async Task<AudioRouteObservation> FollowBluetoothAsync(AudioRouteObservation observation,CancellationToken token)
    {
        var current=observation.Bluetooth??[];
        var arrived=bluetooth is null?null:current.Where(name=>!bluetooth.Contains(name)).Order(StringComparer.Ordinal).FirstOrDefault();
        if(arrived is not null&&Value(observation.Configured,"output")!=arrived)
        {
            journal["output"]=journal.TryGetValue("output",out var owner)?owner with{Phase="overridden"}:new(Target("output"),Value(observation.Configured,"output"),Value(observation.Effective,"output"),"overridden");Save();
            await backend.SetConfiguredAsync("output",arrived,token);
            observation=await ObserveUntilAsync("output",arrived,arrived,token);
        }
        bluetooth=current;return observation;
    }
    object State(string kind,bool wanted,AudioRouteObservation? observation,string? error)
    {
        var current=observation is null?null:Value(observation.Effective,kind);
        journal.TryGetValue(kind,out var owner);
        return new{state=error is not null?"error":current==Target(kind)?"active":owner?.Phase=="overridden"?"manual-override":"inactive",enabled=wanted,effective=current,configured=observation is null?null:Value(observation.Configured,kind),owned=owner?.Phase=="owned",previous=owner?.PreviousEffective,error};
    }
    public async Task RefreshAsync(bool output,bool microphone,bool outputAvailable,bool microphoneAvailable,bool mutate=true,bool explicitOutput=false,bool explicitMicrophone=false,CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try
        {
            AudioRouteObservation? observation=null;string? outputError=null,microphoneError=null;
            try{observation=await backend.ReadAsync(token);}catch(Exception e){outputError=microphoneError=e.Message;}
            if(observation is not null&&mutate)
            {
                try{observation=await FollowBluetoothAsync(observation,token);}catch(Exception e){outputError=e.Message;try{observation=await backend.ReadAsync(token);}catch{}}
                try{observation=await ReconcileAsync("output",output,explicitOutput,outputAvailable,observation,token);}catch(Exception e){outputError=e.Message;try{observation=await backend.ReadAsync(token);}catch{}}
                try{observation=await ReconcileAsync("microphone",microphone,explicitMicrophone,microphoneAvailable,observation,token);}catch(Exception e){microphoneError=e.Message;try{observation=await backend.ReadAsync(token);}catch{}}
            }
            Snapshot=new{output=State("output",output,observation,outputError),microphone=State("microphone",microphone,observation,microphoneError)};
            if((explicitOutput&&outputError is not null)||(explicitMicrophone&&microphoneError is not null))throw new IOException(outputError??microphoneError);
        }
        finally{gate.Release();}
    }
    public async Task ShutdownAsync(CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try
        {
            if(journal.Count==0)return;
            var observation=await backend.ReadAsync(token);
            var failures=new List<Exception>();
            foreach(var kind in new[]{"output","microphone"})
            {
                if(!journal.TryGetValue(kind,out var owner)||owner.Phase=="overridden")continue;
                try
                {
                    if(Value(observation.Configured,kind)==owner.Target)
                    {
                        await backend.SetConfiguredAsync(kind,owner.PreviousConfigured,token);
                        var expected=owner.PreviousConfigured??owner.PreviousEffective;
                        observation=await ObserveUntilAsync(kind,owner.PreviousConfigured,expected is not null&&observation.Nodes.Contains(expected)?expected:null,token);
                        journal.Remove(kind);
                    }
                    else if((owner.Phase is "pending" or "suspended")&&Value(observation.Configured,kind)==owner.PreviousConfigured)journal.Remove(kind);
                    else journal[kind]=owner with{Phase="overridden"};
                    Save();
                }
                catch(Exception error)
                {
                    // One endpoint's failure must not leave the other route
                    // attached to an engine which is about to shut down.
                    failures.Add(error);
                    try{observation=await backend.ReadAsync(token);}catch{}
                }
            }
            if(failures.Count>0)throw new IOException("일부 기본 오디오 장치를 복원할 수 없습니다: "+string.Join("; ",failures.Select(e=>e.Message)),new AggregateException(failures));
        }
        finally{gate.Release();}
    }
}
