using System.Text.Json;
namespace PersonalTools;
public sealed record VllmConfig(int Context=8192,int MaxSequences=1,int CpuOffloadGiB=0);
public sealed class VllmController
{
    const string Unit="personal-tools-vllm.service";
    readonly SemaphoreSlim gate=new(1);
    VllmConfig config=LinuxPaths.Read(Path.Combine(LinuxPaths.Config,"vllm.json"),new VllmConfig());
    string status="stopped",gpu="";string? error;object[] steps=[];double elapsed;
    public object Snapshot=>new{status,model="qwen3.8-27b",checkpoint="RedHatAI/Qwen3.8-27B-INT4",baseUrl="http://127.0.0.1:8000/v1",context=config.Context,quantization="INT4",kvCache="FP8",maxSequences=config.MaxSequences,cpuOffloadGiB=config.CpuOffloadGiB,steps,elapsedSeconds=elapsed,gpu,error};
    public async Task RefreshAsync(CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try {await RefreshCoreAsync(token);}
        catch(Exception e){status="unavailable";error=e.Message;}
        finally{gate.Release();}
    }
    async Task RefreshCoreAsync(CancellationToken token)
    {
            var driver=await ProcessRunner.RunAsync("nvidia-smi",["--query-gpu=name","--format=csv,noheader"],token,10);
            if(driver.ExitCode!=0){status="unavailable";error=(driver.Error+driver.Output).Trim();return;}
            gpu=driver.Output.Trim();
            var result=await ProcessRunner.RunAsync("systemctl",["--user","show",Unit,"--property=ActiveState,LoadState,SubState,MainPID","--no-pager"],token,10);
            if(result.Output.Contains("LoadState=not-found",StringComparison.Ordinal)){status="unavailable";error="vLLM 환경이 설치되지 않았습니다. 제공된 runtime.py install --download-model을 실행해 주세요.";return;}
            bool active=result.Output.Contains("ActiveState=active",StringComparison.Ordinal)||result.Output.Contains("ActiveState=activating",StringComparison.Ordinal);
            if(!active){status=result.Output.Contains("ActiveState=failed",StringComparison.Ordinal)?"error":"stopped";error=status=="error"?ReadRuntimeError():null;config=LinuxPaths.Read(Path.Combine(LinuxPaths.Config,"vllm.json"),new VllmConfig());return;}
            string path=Path.Combine(LinuxPaths.State,"vllm/status.json");
            if(!File.Exists(path)){status="starting";error=null;return;}
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path,token));var state=document.RootElement;
            if(!result.Output.Split('\n').Contains("MainPID="+state.GetProperty("pid").GetInt32())){status="starting";error=null;return;}
            if(DateTimeOffset.UtcNow-state.GetProperty("observedAt").GetDateTimeOffset()>TimeSpan.FromSeconds(20)){status="starting";error="서버 상태 보고가 지연되고 있습니다.";return;}
            status=state.GetProperty("status").GetString()!;error=state.TryGetProperty("error",out var e)&&e.ValueKind==JsonValueKind.String?e.GetString():null;
            elapsed=state.GetProperty("elapsedSeconds").GetDouble();steps=state.GetProperty("steps").EnumerateArray().Select(x=>(object)x.Clone()).ToArray();
            var running=Path.Combine(LinuxPaths.State,"vllm/running-config.json");
            if(File.Exists(running)){using var actual=JsonDocument.Parse(await File.ReadAllTextAsync(running,token));if(actual.RootElement.GetProperty("pid").GetInt32()==state.GetProperty("pid").GetInt32())config=actual.RootElement.GetProperty("config").Deserialize<VllmConfig>(LinuxPaths.Json)!;}
    }
    string? ReadRuntimeError(){try{using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(LinuxPaths.State,"vllm/status.json")));return document.RootElement.GetProperty("error").GetString()??"vLLM 서비스가 실패했습니다.";}catch{return "vLLM 서비스가 실패했습니다. 로그를 확인해 주세요.";}}
    public async Task ConfigureAsync(JsonElement value,CancellationToken token)
    {
        await gate.WaitAsync(token);
        try{
        await RefreshCoreAsync(token);
        if(status is "running" or "starting")throw new InvalidOperationException("vLLM을 중지한 뒤 설정을 변경해 주세요.");
        var next=config with{Context=value.TryGetProperty("context",out var c)?c.GetInt32():config.Context,MaxSequences=value.TryGetProperty("maxSequences",out var seq)?seq.GetInt32():config.MaxSequences,CpuOffloadGiB=value.TryGetProperty("cpuOffloadGiB",out var offload)?offload.GetInt32():config.CpuOffloadGiB};
        if(!new[]{8192,16384,32768,65536}.Contains(next.Context)||next.MaxSequences!=1||!new[]{0,2,4,6,8}.Contains(next.CpuOffloadGiB))throw new ArgumentException("컨텍스트는 8K/16K/32K/64K, 동시 요청은 1개, CPU 오프로드는 0/2/4/6/8 GiB입니다. 각 단계는 로컬 검증이 필요합니다.");
        LinuxPaths.Write(Path.Combine(LinuxPaths.Config,"vllm.json"),next);config=next;
        }finally{gate.Release();}
    }
    public async Task StartAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try{
        await RefreshCoreAsync(token);if(status is "running" or "starting")return;if(status=="unavailable")throw new IOException(error);
        var result=await ProcessRunner.RunAsync("systemctl",["--user","start","--no-block",Unit],token,15);
        if(result.ExitCode!=0)throw new IOException(result.Error);status="starting";error=null;
        }finally{gate.Release();}
    }
    public async Task StopAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try{
        status="stopping";
        var result=await ProcessRunner.RunAsync("systemctl",["--user","stop",Unit],token,40);
        if(result.ExitCode!=0)throw new IOException(result.Error);
        var actual=await ProcessRunner.RunAsync("systemctl",["--user","show",Unit,"--property=ActiveState,MainPID","--no-pager"],token,10);
        if(!actual.Output.Split('\n').Contains("MainPID=0")||!actual.Output.Contains("ActiveState=inactive",StringComparison.Ordinal))throw new IOException("vLLM 프로세스 종료가 아직 확인되지 않았습니다.");
        status="stopped";error=null;
        }catch(Exception e){status="error";error=e.Message;throw;}finally{gate.Release();}
    }
    public string Logs()
    {
        var path=Path.Combine(LinuxPaths.State,"vllm/server.log");if(!File.Exists(path))return "아직 vLLM 실행 로그가 없습니다.";
        using var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);stream.Seek(Math.Max(0,stream.Length-256*1024),SeekOrigin.Begin);using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
}
