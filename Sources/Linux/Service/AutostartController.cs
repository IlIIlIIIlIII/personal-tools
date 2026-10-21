namespace PersonalTools;

public sealed class AutostartController
{
    const string Unit="personal-tools.service";
    readonly SemaphoreSlim gate=new(1);
    public bool Enabled{get;private set;}
    public string State{get;private set;}="unknown";
    public string? Error{get;private set;}
    async Task RefreshCoreAsync(CancellationToken token)
    {
        var result=await ProcessRunner.RunAsync("systemctl",["--user","is-enabled",Unit],token,10);
        State=result.Output.Trim();
        if(string.IsNullOrEmpty(State))throw new IOException(string.IsNullOrWhiteSpace(result.Error)?"자동 시작 상태를 조회하지 못했습니다.":result.Error.Trim());
        Enabled=State is "enabled" or "enabled-runtime";
        Error=State=="not-found"?"Personal Tools 사용자 서비스가 설치되지 않았습니다.":null;
    }
    public async Task RefreshAsync(CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try{await RefreshCoreAsync(token);}
        catch(Exception error){State="unavailable";Error=error.Message;}
        finally{gate.Release();}
    }
    public async Task ConfigureAsync(bool enabled,CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try
        {
            await RefreshCoreAsync(token);
            if(Enabled==enabled)return;
            // enable/disable only changes future session startup. Never use --now:
            // toggling this preference must not seize or restart current audio.
            var result=await ProcessRunner.RunAsync("systemctl",["--user",enabled?"enable":"disable",Unit],token,15);
            if(result.ExitCode!=0)throw new IOException(result.Error.Trim());
            await RefreshCoreAsync(token);
            if(Enabled!=enabled)throw new IOException("요청한 자동 시작 상태가 확인되지 않았습니다.");
        }
        finally{gate.Release();}
    }
}
