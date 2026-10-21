using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text.Json;
using PersonalTools;
[assembly:SupportedOSPlatform("linux")]
internal static class AudioRoutesTests
{
    static int checks;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    sealed class Fake : IAudioRouteBackend
    {
        public Dictionary<string,string?> Effective=new(){{"output","speakers"},{"microphone","physical-mic"}};
        public Dictionary<string,string?> Configured=new();public HashSet<string> Nodes=["speakers","headphones","physical-mic","personal-tools-output","personal-tools-mic"];public HashSet<string> Bluetooth=[];
        public int Writes;public bool FailBeforeWrite;public bool FailAfterWrite;public string? FailedKind;
        public Task<AudioRouteObservation> ReadAsync(CancellationToken token)=>Task.FromResult(new AudioRouteObservation(new(Effective),new(Configured),new(Nodes),new(Bluetooth)));
        public Task SetConfiguredAsync(string kind,string? name,CancellationToken token)
        {
            if(FailBeforeWrite||FailedKind==kind)throw new IOException("Test failure before route mutation");
            Writes++;Configured[kind]=name;Effective[kind]=name??(kind=="output"?"speakers":"physical-mic");
            if(FailAfterWrite)throw new IOException("Test failure after route mutation");
            return Task.CompletedTask;
        }
    }
    static async Task Main(string[] args)
    {
        if(args.Contains("--private-live")||args.Contains("--desktop-live")){await Live(args.Contains("--desktop-live"));return;}
        var directory=Path.Combine(Path.GetTempPath(),"pt-route-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            string journal=Path.Combine(directory,"routes.json");var backend=new Fake();var route=new AudioRouting(backend,journal);
            await route.RefreshAsync(true,true,true,true,mutate:false);Check(backend.Writes==0&&!File.Exists(journal),"read-only snapshot never changes defaults or journals");
            await route.RefreshAsync(true,false,true,true,explicitOutput:true);
            Check(backend.Effective["output"]=="personal-tools-output"&&backend.Effective["microphone"]=="physical-mic","EQ acquires only the output route");
            Check(File.ReadAllText(journal).Contains("speakers"),"prior route is durably recorded before use");
            var recovered=new AudioRouting(backend,journal);await recovered.RefreshAsync(true,false,true,true);
            Check(backend.Writes==1,"daemon restart keeps ownership without rewriting route");
            backend.Configured["output"]="headphones";backend.Effective["output"]="headphones";
            await recovered.RefreshAsync(true,false,true,true);await recovered.ShutdownAsync();
            Check(backend.Effective["output"]=="headphones"&&backend.Writes==1,"manual output override survives refresh and shutdown");
            recovered=new AudioRouting(backend,journal);await recovered.RefreshAsync(true,false,true,true);
            Check(backend.Effective["output"]=="headphones","manual override survives daemon restart");
            await recovered.RefreshAsync(false,false,true,true);await recovered.RefreshAsync(true,true,true,true,explicitOutput:true,explicitMicrophone:true);
            Check(backend.Effective["microphone"]=="personal-tools-mic","processed microphone is selected only by explicit intent");
            await recovered.ShutdownAsync();
            Check(backend.Effective["output"]=="headphones"&&backend.Effective["microphone"]=="physical-mic","owned shutdown restores both exact prior defaults");
            Check(backend.Configured["microphone"] is null,"automatic microphone selection remains automatic after restore");
            await recovered.RefreshAsync(true,false,true,true,explicitOutput:true);
            await recovered.RefreshAsync(true,false,false,true);
            Check(backend.Effective["output"]=="headphones","physical disconnect releases a silent virtual default");
            await recovered.RefreshAsync(true,false,true,true);
            Check(backend.Effective["output"]=="personal-tools-output","physical reconnect reacquires the suspended route");
            await recovered.RefreshAsync(true,false,false,true);
            backend.Configured["output"]="speakers";backend.Effective["output"]="speakers";
            await recovered.RefreshAsync(true,false,true,true);await recovered.ShutdownAsync();
            Check(backend.Effective["output"]=="speakers","manual choice while unplugged wins when hardware returns");
            await recovered.RefreshAsync(false,false,true,true);
            backend.Configured["output"]="headphones";backend.Effective["output"]="headphones";
            backend.FailBeforeWrite=true;
            try{await recovered.RefreshAsync(true,false,true,true,explicitOutput:true);throw new Exception("Failed metadata mutation accepted");}catch(IOException){}
            backend.FailBeforeWrite=false;await recovered.RefreshAsync(true,false,true,true);
            Check(backend.Effective["output"]=="personal-tools-output","interrupted pending acquisition is retried safely");
            await recovered.ShutdownAsync();backend.FailAfterWrite=true;
            try{await recovered.RefreshAsync(true,false,true,true,explicitOutput:true);throw new Exception("Failed metadata verification accepted");}catch(IOException){}
            backend.FailAfterWrite=false;recovered=new AudioRouting(backend,journal);await recovered.ShutdownAsync();
            Check(backend.Effective["output"]=="headphones","crash after actual route mutation restores durable prior default");
            backend.Nodes.Remove("personal-tools-output");int writes=backend.Writes;
            try{await recovered.RefreshAsync(true,false,true,true,explicitOutput:true);throw new Exception("Missing endpoint accepted");}catch(IOException){}
            Check(backend.Writes==writes,"missing virtual endpoint never changes desktop output");
            backend.Nodes.Add("personal-tools-output");
            await recovered.RefreshAsync(true,true,true,true,explicitOutput:true,explicitMicrophone:true);
            backend.FailedKind="output";
            try{await recovered.ShutdownAsync();throw new Exception("Failed shutdown restoration accepted");}catch(IOException){}
            Check(backend.Effective["output"]=="personal-tools-output"&&backend.Effective["microphone"]=="physical-mic","output restoration failure does not prevent independent microphone restoration");
            var pending=JsonSerializer.Deserialize<Dictionary<string,AudioRouting.Ownership>>(File.ReadAllText(journal),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Check(pending.Count==1&&pending["output"].PreviousEffective=="headphones","only the failed route retains durable recovery ownership");
            backend.FailedKind=null;recovered=new AudioRouting(backend,journal);await recovered.ShutdownAsync();
            Check(backend.Effective["output"]=="headphones","a later shutdown retries the failed route and restores its exact prior default");
            var bluetooth=new Fake();bluetooth.Nodes.Add("earbuds");bluetooth.Bluetooth.Add("earbuds");
            var follower=new AudioRouting(bluetooth,Path.Combine(directory,"bluetooth.json"));
            await follower.RefreshAsync(true,false,true,true,explicitOutput:true);
            Check(bluetooth.Effective["output"]=="personal-tools-output","headphones already connected at startup do not take over");
            bluetooth.Bluetooth.Clear();await follower.RefreshAsync(true,false,true,true);
            bluetooth.Bluetooth.Add("earbuds");await follower.RefreshAsync(true,false,true,true,mutate:false);
            Check(bluetooth.Effective["output"]=="personal-tools-output","read-only refresh never follows headphones");
            await follower.RefreshAsync(true,false,true,true);
            Check(bluetooth.Effective["output"]=="earbuds","newly connected headphones become the default output");
            int followed=bluetooth.Writes;await follower.RefreshAsync(true,false,true,true);
            Check(bluetooth.Effective["output"]=="earbuds"&&bluetooth.Writes==followed,"headphones are followed once without automatic EQ reacquisition");
            bluetooth.Configured["output"]="speakers";bluetooth.Effective["output"]="speakers";await follower.RefreshAsync(true,false,true,true);
            Check(bluetooth.Effective["output"]=="speakers","a manual choice is kept while headphones stay connected");
            await follower.RefreshAsync(true,false,true,true,explicitOutput:true);
            Check(bluetooth.Effective["output"]=="personal-tools-output","an explicit EQ request reacquires the processed output");
            var unowned=new Fake();unowned.Configured["output"]=unowned.Effective["output"]="personal-tools-output";
            var selected=new AudioRouting(unowned,Path.Combine(directory,"unowned.json"));await selected.RefreshAsync(true,false,true,true);
            unowned.Nodes.Add("earbuds");unowned.Bluetooth.Add("earbuds");await selected.RefreshAsync(true,false,true,true);
            Check(unowned.Effective["output"]=="earbuds","headphones also win over a processed output selected outside the service");
            Console.WriteLine($"All {checks} ownership checks passed.");
        }
        finally{Directory.Delete(directory,true);}
    }
    static async Task Live(bool desktop)
    {
        // The launcher creates a private PipeWire/WirePlumber policy instance
        // containing synthetic devices only. Never permit this mode on the host.
        string? directory=Environment.GetEnvironmentVariable(desktop?"PERSONALTOOLS_DESKTOP_ROUTE_TEST":"PERSONALTOOLS_PRIVATE_AUDIO_TEST");
        if(directory is null||(!desktop&&Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!=directory))throw new Exception("Explicit audio test environment is required.");
        var backend=new PipeWireRouteBackend();var before=await backend.ReadAsync(default);
        Check(before.Effective.TryGetValue("output",out var initial)&&initial is not null&&before.Nodes.Contains(initial)&&before.Nodes.Contains("personal-tools-output")&&before.Nodes.Contains("personal-tools-mic"),"original and processed endpoints are ready");
        var route=new AudioRouting(backend,Path.Combine(directory,"routes.json"));
        using var playback=new Process{StartInfo=new("pw-play"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true}};
        foreach(var argument in new[]{"--rate","48000","--channels","2","--format","f32","--raw","/dev/zero"})playback.StartInfo.ArgumentList.Add(argument);
        playback.Start();
        try{
            await StreamTarget(playback.Id,before.Effective["output"]!);
            Check(true,"a real existing playback stream begins on the original output");
            await route.RefreshAsync(true,false,true,true,explicitOutput:true);
            Check((await backend.ReadAsync(default)).Effective["output"]=="personal-tools-output","real WirePlumber applies configured output metadata");
            await StreamTarget(playback.Id,"personal-tools-output");
            Check(true,"an existing playback stream follows the processed output route");
            await route.RefreshAsync(true,true,true,true,explicitMicrophone:true);
            Check((await backend.ReadAsync(default)).Effective["microphone"]=="personal-tools-mic","real WirePlumber selects processed microphone");
            await route.ShutdownAsync();await StreamTarget(playback.Id,before.Effective["output"]!);
            Check(true,"existing playback follows the restored original output");
        }finally{try{await route.ShutdownAsync();}finally{if(!playback.HasExited){playback.Kill();await playback.WaitForExitAsync();}}}
        var after=await backend.ReadAsync(default);
        Check(after.Effective["output"]==before.Effective["output"]&&after.Effective["microphone"]==before.Effective["microphone"],"real defaults return to their original nodes");
        Check(after.Configured.GetValueOrDefault("output")==before.Configured.GetValueOrDefault("output")&&after.Configured.GetValueOrDefault("microphone")==before.Configured.GetValueOrDefault("microphone"),"real configured-default absence is restored without pinning a new default");
        if(!desktop)await LiveBluetooth(backend,directory,after);
        Console.WriteLine($"All {checks} {(desktop?"desktop":"private")} WirePlumber checks passed.");
    }
    static async Task LiveBluetooth(PipeWireRouteBackend backend,string directory,AudioRouteObservation before)
    {
        // Synthetic headphones laid out like WirePlumber's: an internal device
        // node behind a loopback sink. A low priority proves the switch comes
        // from the service, not from policy.
        var follower=new AudioRouting(backend,Path.Combine(directory,"bluetooth.json"));await follower.RefreshAsync(false,false,true,true);
        async Task Create(string name,string props)
        {
            await ProcessRunner.RunAsync("pw-cli",["create-node","adapter","{ factory.name=support.null-audio-sink node.name="+name+" media.class=Audio/Sink "+props+" audio.channels=2 audio.position=[ FL FR ] object.linger=true priority.session=10 }"],seconds:5);
            for(int i=0;i<100&&!(await backend.ReadAsync(default)).Nodes.Contains(name);i++)await Task.Delay(50);
        }
        try
        {
            await Create("bluez_output_internal.test","device.api=bluez5 api.bluez5.internal=true");
            await follower.RefreshAsync(false,false,true,true);
            Check((await backend.ReadAsync(default)).Configured.GetValueOrDefault("output")==before.Configured.GetValueOrDefault("output"),"internal Bluetooth nodes are not followed");
            await Create("bluez_output.test","bluez5.sink-loopback=true");
            await follower.RefreshAsync(false,false,true,true);
            Check((await backend.ReadAsync(default)).Effective["output"]=="bluez_output.test","real WirePlumber follows newly connected headphones");
        }
        finally
        {
            await backend.SetConfiguredAsync("output",before.Configured.GetValueOrDefault("output"),default);
            var result=await ProcessRunner.RunAsync("pw-dump",[],seconds:5);using var document=JsonDocument.Parse(result.Output);
            foreach(var node in document.RootElement.EnumerateArray().Where(x=>x.TryGetProperty("info",out var info)&&info.TryGetProperty("props",out var props)&&props.TryGetProperty("node.name",out var name)&&name.GetString()!.StartsWith("bluez_output")))
                await ProcessRunner.RunAsync("pw-cli",["destroy",node.GetProperty("id").ToString()],seconds:5);
        }
    }
    static async Task StreamTarget(int processId,string name)
    {
        string last="";
        for(int attempt=0;attempt<100;attempt++)
        {
            var result=await ProcessRunner.RunAsync("pw-dump",[],seconds:5);using var document=JsonDocument.Parse(result.Output);
            var nodes=document.RootElement.EnumerateArray().Where(x=>x.GetProperty("type").GetString()=="PipeWire:Interface:Node").ToArray();
            var clients=document.RootElement.EnumerateArray().Where(x=>x.GetProperty("type").GetString()=="PipeWire:Interface:Client"&&x.TryGetProperty("info",out var info)&&info.TryGetProperty("props",out var props)&&props.TryGetProperty("application.process.id",out var pid)&&pid.ToString()==processId.ToString()).Select(x=>x.GetProperty("id").ToString()).ToHashSet();
            var sources=nodes.Where(x=>x.TryGetProperty("info",out var info)&&info.TryGetProperty("props",out var props)&&((props.TryGetProperty("application.process.id",out var pid)&&pid.ToString()==processId.ToString())||(props.TryGetProperty("client.id",out var client)&&clients.Contains(client.ToString())))).Select(x=>x.GetProperty("id").GetInt32()).ToHashSet();
            var targets=nodes.Where(x=>x.TryGetProperty("info",out var info)&&info.TryGetProperty("props",out var props)&&props.TryGetProperty("node.name",out var node)&&node.GetString()==name).Select(x=>x.GetProperty("id").GetInt32()).ToHashSet();
            bool linked=document.RootElement.EnumerateArray().Any(x=>x.GetProperty("type").GetString()=="PipeWire:Interface:Link"&&x.TryGetProperty("info",out var link)&&sources.Contains(link.GetProperty("output-node-id").GetInt32())&&targets.Contains(link.GetProperty("input-node-id").GetInt32())&&link.GetProperty("state").GetString()=="active");
            last=JsonSerializer.Serialize(new{sources,targets,links=document.RootElement.EnumerateArray().Where(x=>x.GetProperty("type").GetString()=="PipeWire:Interface:Link").Select(x=>x.GetProperty("info"))});
            if(linked)return;await Task.Delay(50);
        }
        throw new Exception($"Playback process {processId} did not link to {name}: {last}");
    }
}
