using Tmds.DBus;

namespace PersonalTools;

[DBusInterface("org.gnome.Shell.Extensions")]
public interface IGnomeShellExtensions:IDBusObject
{
    Task<IDictionary<string,object>> GetExtensionInfoAsync(string uuid);
    Task<bool> EnableExtensionAsync(string uuid);
    Task<bool> DisableExtensionAsync(string uuid);
}

public sealed class ShellExtensionController
{
    public const string Uuid="personal-tools@griplabs.io";
    readonly IGnomeShellExtensions extensions;
    readonly SemaphoreSlim gate=new(1);
    public bool Available{get;private set;}
    public bool Enabled{get;private set;}
    public bool CanChange{get;private set;}
    public string State{get;private set;}="unavailable";
    public string? Error{get;private set;}
    public ShellExtensionController(Connection session)=>extensions=session.CreateProxy<IGnomeShellExtensions>("org.gnome.Shell.Extensions","/org/gnome/Shell/Extensions");

    async Task RefreshCoreAsync()
    {
        var info=await extensions.GetExtensionInfoAsync(Uuid).WaitAsync(TimeSpan.FromSeconds(4));
        Available=info.Count>0;CanChange=Available&&(!info.TryGetValue("canChange",out var change)||change is true);
        int state=info.TryGetValue("state",out var value)?Convert.ToInt32(value):0;
        // The enabled preference can stay true after a load failure. Reflect the
        // actual Shell state, independently from the extension's D-Bus heartbeat.
        Enabled=state==1;
        State=state switch{1=>"enabled",2=>"disabled",3=>"error",4=>"out-of-date",5 or 6=>"starting",_=>"unavailable"};
        Error=info.TryGetValue("error",out var error)&&!string.IsNullOrWhiteSpace(error.ToString())?error.ToString():null;
        if(!Available)Error="GNOME에서 Personal Tools 확장을 찾지 못했습니다. 설치 직후라면 다시 로그인해 주세요.";
    }
    public async Task RefreshAsync()
    {
        await gate.WaitAsync();
        try{await RefreshCoreAsync();}
        catch(Exception error){Available=false;Enabled=false;CanChange=false;State="unavailable";Error=error.Message;}
        finally{gate.Release();}
    }
    public async Task ConfigureAsync(bool enabled)
    {
        await gate.WaitAsync();
        try
        {
            await RefreshCoreAsync();
            if(!Available||!CanChange)throw new IOException(Error??"GNOME에서 이 확장을 변경할 수 없습니다.");
            if(Enabled==enabled)return;
            bool accepted=await (enabled?extensions.EnableExtensionAsync(Uuid):extensions.DisableExtensionAsync(Uuid)).WaitAsync(TimeSpan.FromSeconds(10));
            if(!accepted)throw new IOException("GNOME에서 확장 상태 변경을 거부했습니다. 확장 오류와 전체 확장 사용 설정을 확인해 주세요.");
            for(int attempt=0;attempt<30;attempt++)
            {
                await RefreshCoreAsync();
                if(Enabled==enabled)return;
                if(State is "error" or "out-of-date" or "unavailable")break;
                await Task.Delay(100);
            }
            throw new IOException(Error??"요청한 GNOME 확장 상태가 확인되지 않았습니다.");
        }
        finally{gate.Release();}
    }
}
