using System.Diagnostics;
using System.Text.Json;
using Tmds.DBus;
using Spec = (string Connector,string Vendor,string Product,string Serial);
using Mode = (string Id,int Width,int Height,double Rate,double PreferredScale,double[] Scales,System.Collections.Generic.IDictionary<string,object> Properties);
using Monitor = ((string Connector,string Vendor,string Product,string Serial) Spec,(string Id,int Width,int Height,double Rate,double PreferredScale,double[] Scales,System.Collections.Generic.IDictionary<string,object> Properties)[] Modes,System.Collections.Generic.IDictionary<string,object> Properties);
using Logical = (int X,int Y,double Scale,uint Transform,bool Primary,(string Connector,string Vendor,string Product,string Serial)[] Monitors,System.Collections.Generic.IDictionary<string,object> Properties);
using ApplyLogical = (int X,int Y,double Scale,uint Transform,bool Primary,(string Connector,string Mode,System.Collections.Generic.IDictionary<string,object> Properties)[] Monitors);
namespace PersonalTools;
[DBusInterface("org.gnome.Mutter.DisplayConfig")]
public interface IMutterDisplay : IDBusObject
{
    Task<(uint Serial, Monitor[] Monitors,Logical[] Logical,IDictionary<string,object> Properties)> GetCurrentStateAsync();
    Task ApplyMonitorsConfigAsync(uint serial,uint method,ApplyLogical[] monitors,IDictionary<string,object> properties);
}
[DBusInterface("org.gnome.SessionManager")]
public interface ISessionManager : IDBusObject { Task<ObjectPath[]> GetInhibitorsAsync(); }
[DBusInterface("org.gnome.SessionManager.Inhibitor")]
public interface ISessionInhibitor : IDBusObject { Task<string> GetAppIdAsync(); Task<string> GetReasonAsync(); Task<uint> GetFlagsAsync(); }
[DBusInterface("org.freedesktop.login1.Manager")]
public interface ILoginManager : IDBusObject { Task<(string What,string Who,string Why,string Mode,uint Uid,uint Pid)[]> ListInhibitorsAsync(); }
public sealed record DisplayRule(string Id,string Name,string Executable,string? DesktopId=null,bool Enabled=true);
public sealed record DisplayConfig(bool AutoHdr=false,DisplayRule[]? Rules=null);
public sealed class DesktopController : IHdrDisplay
{
    readonly Connection session;
    readonly MonitorPowerController monitorPower=new();
    readonly SemaphoreSlim gate=new(1);
    readonly IMutterDisplay mutter;
    readonly HdrAutomation automation;
    DisplayConfig config;
    object[] inhibitors=[],sleepInhibitors=[];
    (uint Serial, Monitor[] Monitors,Logical[] Logical,IDictionary<string,object> Properties)? current;
    string? error; DateTimeOffset? observed;
    string inhibitorStatus="unknown",sleepInhibitorStatus="unknown";
    string? inhibitorError,sleepInhibitorError;DateTimeOffset? inhibitorsObservedAt,sleepInhibitorsObservedAt;
    static string ConfigPath=>Path.Combine(LinuxPaths.Config,"display.json");
    public DesktopController(Connection session)
    {
        this.session=session; mutter=session.CreateProxy<IMutterDisplay>("org.gnome.Mutter.DisplayConfig","/org/gnome/Mutter/DisplayConfig");
        config=LinuxPaths.Read(ConfigPath,new DisplayConfig()); automation=new(this,new HdrJournal());
    }
    static string Id(Spec spec)=>string.Join("|",spec.Connector,spec.Vendor,spec.Product,spec.Serial);
    static uint Color(IDictionary<string,object> p)=>p.TryGetValue("color-mode",out var value)?Convert.ToUInt32(value):0;
    static bool Supports(IDictionary<string,object> p)=>p.TryGetValue("supported-color-modes",out var value)&&value is uint[] modes&&modes.Contains(1u);
    public object Snapshot => new {status=current is null?"unavailable":"ready",error,monitors=current is { } value?value.Monitors.Select(m=>{
        var mode=m.Modes.FirstOrDefault(x=>x.Properties.TryGetValue("is-current",out var active)&&active is true);
        return (object)new {id=Id(m.Spec),name=m.Properties.TryGetValue("display-name",out var name)?name.ToString():m.Spec.Product,connector=m.Spec.Connector,width=mode.Width,height=mode.Height,refreshRate=mode.Rate,primary=value.Logical.Any(l=>l.Primary&&l.Monitors.Contains(m.Spec)),hdrSupported=Supports(m.Properties),hdrEnabled=Color(m.Properties)==1};
    }).ToArray():[], monitorPower=monitorPower.Snapshot, autoHdr=config.AutoHdr,rules=config.Rules??[],inhibitors,sleepInhibitors,observedAt=observed,inhibitorStatus,sleepInhibitorStatus,inhibitorError,sleepInhibitorError,inhibitorsObservedAt,sleepInhibitorsObservedAt};
    // HdrAutomation is run on the pool under gate; the D-Bus connection has no UI synchronization context.
    public HdrMonitor[] Read()
    {
        current=mutter.GetCurrentStateAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        var state=current.Value;
        return state.Monitors.Select(m=>new HdrMonitor(Id(m.Spec),m.Spec.Product,state.Logical.Any(l=>l.Primary&&l.Monitors.Contains(m.Spec)),Supports(m.Properties),Color(m.Properties)==1)).ToArray();
    }
    public void Set(string id,bool enabled)
    {
        var state=mutter.GetCurrentStateAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        var target=state.Monitors.FirstOrDefault(x=>Id(x.Spec)==id);
        if(target.Spec.Connector is null)throw new IOException("모니터가 연결 해제되었습니다.");
        if(enabled&&!Supports(target.Properties))throw new IOException("이 모니터는 BT.2100 HDR을 지원하지 않습니다.");
        if(!state.Logical.Any(l=>l.Monitors.Contains(target.Spec)))throw new IOException("비활성 모니터는 변경할 수 없습니다.");
        var layouts=state.Logical.Select(l=>{
            var monitors=l.Monitors.Select(spec=>{
                var m=state.Monitors.Single(x=>x.Spec==spec);
                var mode=m.Modes.Single(x=>x.Properties.TryGetValue("is-current",out var active)&&active is true);
                IDictionary<string,object> props=new Dictionary<string,object>{{"color-mode",Id(spec)==id?(enabled?1u:0u):Color(m.Properties)}};
                foreach(var key in new[]{"underscanning","rgb-range"})if(m.Properties.TryGetValue(key,out var prop))props[key]=prop;
                return (spec.Connector,mode.Id,props);
            }).ToArray();
            return (l.X,l.Y,l.Scale,l.Transform,l.Primary,monitors);
        }).ToArray();
        IDictionary<string,object> global=new Dictionary<string,object>();
        if(state.Properties.TryGetValue("layout-mode",out var layoutMode))global["layout-mode"]=layoutMode;
        if(state.Properties.TryGetValue("monitors-for-lease",out var lease))global["monitors-for-lease"]=lease;
        mutter.ApplyMonitorsConfigAsync(state.Serial,1,layouts,global).WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
        Read();
        if(Read().Single(x=>x.Id==id).Enabled!=enabled)throw new IOException("Mutter에서 HDR 변경이 확인되지 않았습니다.");
    }
    static HdrProcess[] Processes()
    {
        var result=new List<HdrProcess>();
        foreach(var dir in Directory.EnumerateDirectories("/proc"))
        {
            if(!int.TryParse(Path.GetFileName(dir),out var pid))continue;
            try {
                var path=File.ResolveLinkTarget(Path.Combine(dir,"exe"),true)?.FullName;
                if(path is null)continue;
                var stat=File.ReadAllText(Path.Combine(dir,"stat")); var tail=stat[(stat.LastIndexOf(')')+2)..].Split(' ');
                // proc stat field22 is starttime: PID reuse never inherits ownership.
                result.Add(new($"{pid}:{tail[19]}",path));
            }catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){}
        }
        return result.ToArray();
    }
    public async Task RefreshAsync(bool reconcile=true)
    {
        await monitorPower.RefreshAsync();
        await gate.WaitAsync();
        try {
            await Task.Run(()=>{
                Read();
                if(reconcile)automation.Reconcile(Processes(),config.AutoHdr?(config.Rules??[]).Select(r=>new HdrProgram(Guid.Parse(r.Id),r.Name,r.Executable,r.Enabled)).ToArray():[]);
            });
            error=automation.Error;observed=DateTimeOffset.UtcNow;
        }catch(Exception e){error=e.Message;}
        finally{gate.Release();}
        var found=new List<object>();var sleep=new List<object>();
        try {
            var manager=session.CreateProxy<ISessionManager>("org.gnome.SessionManager","/org/gnome/SessionManager");
            foreach(var path in await manager.GetInhibitorsAsync().WaitAsync(TimeSpan.FromSeconds(5))) {
                var item=session.CreateProxy<ISessionInhibitor>("org.gnome.SessionManager",path);
                var app=await item.GetAppIdAsync();var reason=await item.GetReasonAsync();var flags=await item.GetFlagsAsync();
                if((flags&8)!=0)found.Add(new{id=path.ToString(),appId=app,appName=app,reason,source="gnome-session",scope="idle"});
            }
            inhibitors=found.ToArray();inhibitorStatus="ready";inhibitorError=null;inhibitorsObservedAt=DateTimeOffset.UtcNow;
        }catch(Exception e){inhibitorStatus=inhibitorsObservedAt is null?"unavailable":"stale";inhibitorError=e.Message;}
        try {
            var manager=Connection.System.CreateProxy<ILoginManager>("org.freedesktop.login1","/org/freedesktop/login1");
            foreach(var item in await manager.ListInhibitorsAsync().WaitAsync(TimeSpan.FromSeconds(5))) {
                var entry=new{id=$"logind:{item.Pid}:{item.What}:{item.Mode}",appId=item.Pid.ToString(),appName=item.Who,reason=item.Why,source="logind",scope=item.What+":"+item.Mode};
                if(item.What.Split(':').Contains("sleep"))sleep.Add(entry);
            }
            sleepInhibitors=sleep.ToArray();sleepInhibitorStatus="ready";sleepInhibitorError=null;sleepInhibitorsObservedAt=DateTimeOffset.UtcNow;
        }catch(Exception e){sleepInhibitorStatus=sleepInhibitorsObservedAt is null?"unavailable":"stale";sleepInhibitorError=e.Message;}
    }
    public async Task ExecuteAsync(string action,JsonElement payload)
    {
        if(action=="refresh"){await RefreshAsync();return;}
        await gate.WaitAsync();
        try {
            switch(action) {
                case "monitor-power": await monitorPower.SetAsync(payload.GetProperty("enabled").GetBoolean());break;
                case "hdr": await Task.Run(()=>automation.SetManual(payload.GetProperty("id").GetString()!,payload.GetProperty("enabled").GetBoolean()));break;
                case "auto-hdr": config=config with{AutoHdr=payload.GetProperty("enabled").GetBoolean()};LinuxPaths.Write(ConfigPath,config);break;
                case "save-rule":
                    string id=payload.TryGetProperty("id",out var rid)&&!string.IsNullOrEmpty(rid.GetString())?rid.GetString()!:Guid.NewGuid().ToString("N");
                    if(!Guid.TryParse(id,out _))throw new ArgumentException("Invalid rule ID.");
                    string path=payload.GetProperty("executable").GetString()!, name=payload.GetProperty("name").GetString()!.Trim();
                    if(!Path.IsPathFullyQualified(path)||!File.Exists(path)||name.Length is 0 or >120)throw new ArgumentException("실행 파일의 절대 경로와 이름을 확인해 주세요.");
                    path=File.ResolveLinkTarget(path,true)?.FullName??Path.GetFullPath(path);
                    var rule=new DisplayRule(id,name,path,payload.TryGetProperty("desktopId",out var desk)?desk.GetString():null,!payload.TryGetProperty("enabled",out var enable)||enable.GetBoolean());
                    config=config with{Rules=[..(config.Rules??[]).Where(r=>r.Id!=id),rule]};LinuxPaths.Write(ConfigPath,config);break;
                case "delete-rule":config=config with{Rules=(config.Rules??[]).Where(r=>r.Id!=payload.GetProperty("id").GetString()).ToArray()};LinuxPaths.Write(ConfigPath,config);break;
                default:throw new ArgumentException("Unknown display action.");
            }
        }finally{gate.Release();}
        await RefreshAsync();
    }
    public async Task ShutdownAsync(){await gate.WaitAsync();try{await Task.Run(automation.Shutdown);}finally{gate.Release();}}
}
