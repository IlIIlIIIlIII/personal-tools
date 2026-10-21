using System.Text.Json;
namespace PersonalTools;

// The separately managed DDC guard survives Personal Tools restarts.
public sealed class MonitorPowerController
{
    readonly SemaphoreSlim gate=new(1);
    readonly string home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    bool available,enabled,running;
    string status="unknown";
    string? error;
    public object Snapshot=>new{available,enabled,running,status,error,timeoutSeconds=60,target="MO32U24 · 2대"};
    string Command=>Path.Combine(home,".local/bin/mo32-power");
    public async Task RefreshAsync()
    {
        await gate.WaitAsync();
        try{await ReadAsync();}finally{gate.Release();}
    }
    async Task ReadAsync()
    {
        available=File.Exists(Command);
        if(!available){enabled=running=false;status="unavailable";error="MO32U24 전원 제어 도우미가 설치되어 있지 않습니다.";return;}
        try{
            var state=await ProcessRunner.RunAsync("systemctl",["--user","show","mo32-power.service","--property=UnitFileState","--property=ActiveState","--property=LoadState"],seconds:5);
            if(state.ExitCode!=0)throw new IOException(state.Error);
            var fields=state.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>s.Split('=',2)).Where(v=>v.Length==2).ToDictionary(v=>v[0],v=>v[1]);
            available=fields.GetValueOrDefault("LoadState")=="loaded";
            enabled=fields.GetValueOrDefault("UnitFileState") is "enabled" or "enabled-runtime";
            running=fields.GetValueOrDefault("ActiveState")=="active";
            error=null;status=enabled?"running":"off";
            if(!available)throw new IOException("MO32U24 전원 제어 서비스가 설치되어 있지 않습니다.");
            if(enabled){
                if(!running)throw new IOException("전원 제어 서비스가 실행 중이 아닙니다.");
                var path=Path.Combine(home,".local/share/mo32-power/status.json");
                if(!File.Exists(path)||DateTime.UtcNow-File.GetLastWriteTimeUtc(path)>TimeSpan.FromSeconds(15))throw new IOException("전원 제어 상태가 갱신되지 않습니다.");
                using var json=JsonDocument.Parse(await File.ReadAllTextAsync(path));
                if(!json.RootElement.TryGetProperty("active",out var active)||!active.GetBoolean())throw new IOException("전원 제어를 시작하고 있습니다.");
            }
        }catch(Exception e){status="error";error=e.Message;}
    }
    public async Task SetAsync(bool value)
    {
        await gate.WaitAsync();
        try{
            if(!File.Exists(Command))throw new IOException("MO32U24 전원 제어 도우미가 설치되어 있지 않습니다.");
            var result=await ProcessRunner.RunAsync(Command,[value?"hold":"release"],seconds:120);
            if(result.ExitCode!=0)throw new IOException(string.IsNullOrWhiteSpace(result.Error)?result.Output:result.Error);
        }finally{await ReadAsync();gate.Release();}
    }
}
