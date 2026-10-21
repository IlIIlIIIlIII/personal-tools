using System.Diagnostics;
using System.Text.Json;
using PersonalTools;
using Tmds.DBus;

[DBusInterface("org.gnome.SessionManager")]
public interface ILiveSessionManager:IDBusObject
{
    Task<uint> InhibitAsync(string appId,uint window,string reason,uint flags);
    Task UninhibitAsync(uint cookie);
}

internal static class LiveDesktop
{
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,LinuxPaths.Json);
    public static async Task RestoreAsync(string path)
    {
        // Separate process recovery also works when a hardware test was killed
        // before its managed finally block could run.
        string isolated=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,"test-xdg");
        foreach(var name in new[]{"XDG_CONFIG_HOME","XDG_DATA_HOME","XDG_STATE_HOME","XDG_CACHE_HOME"})Environment.SetEnvironmentVariable(name,Path.Combine(isolated,name));
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var original=document.RootElement.GetProperty("monitors").Deserialize<HdrMonitor[]>(LinuxPaths.Json)!;
        using var session=new Connection(new ClientConnectionOptions(Address.Session){RunContinuationsAsynchronously=true});await session.ConnectAsync();
        var desktop=new DesktopController(session);
        foreach(var monitor in original)
        {
            var actual=desktop.Read().Single(x=>x.Id==monitor.Id);
            if(actual.Enabled!=monitor.Enabled)desktop.Set(monitor.Id,monitor.Enabled);
        }
        if(original.Any(m=>desktop.Read().Single(x=>x.Id==m.Id).Enabled!=m.Enabled))throw new IOException("Saved HDR states did not restore");
        Console.WriteLine("PASS saved HDR states recovered");
    }
    public static async Task RunAsync(bool exerciseHdr)
    {
        string output=Path.GetFullPath(Environment.GetEnvironmentVariable("PERSONALTOOLS_DESKTOP_REPORT")??"build/linux/desktop-qa/result.json");
        string isolated=Path.Combine(Path.GetDirectoryName(output)!,"test-xdg");
        foreach(var name in new[]{"XDG_CONFIG_HOME","XDG_DATA_HOME","XDG_STATE_HOME","XDG_CACHE_HOME"})Environment.SetEnvironmentVariable(name,Path.Combine(isolated,name));
        // This harness instantiates only the desktop adapter; it never owns audio.
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var checks=new List<object>();var failures=new List<string>();var before=new List<object>();bool completed=false;
        void Save()=>LinuxPaths.Write(output,new{observedAt=DateTimeOffset.UtcNow,scope="Actual GNOME session/logind inhibitors and optional reversible HDR; isolated test settings",checks,failures,baseline=before,passed=completed&&failures.Count==0});
        void Check(bool condition,string name,object? detail=null){checks.Add(new{name,passed=condition,detail});Save();if(!condition)throw new IOException(name);Console.WriteLine("PASS "+name);}
        using var session=new Connection(new ClientConnectionOptions(Address.Session){RunContinuationsAsynchronously=true});await session.ConnectAsync();
        var desktop=new DesktopController(session);
        var mutter=session.CreateProxy<IMutterDisplay>("org.gnome.Mutter.DisplayConfig","/org/gnome/Mutter/DisplayConfig");
        var manager=session.CreateProxy<ILiveSessionManager>("org.gnome.SessionManager","/org/gnome/SessionManager");
        uint? cookie=null;Process? inhibitor=null;var children=new List<Process>();HdrMonitor[] original=[];
        async Task<JsonElement> Read(){await desktop.RefreshAsync(false);return Json(desktop.Snapshot);}
        bool Contains(JsonElement snapshot,string group,string reason)=>snapshot.GetProperty(group).EnumerateArray().Any(x=>x.GetProperty("reason").GetString()==reason);
        try
        {
            var initial=await Read();original=desktop.Read();
            before.AddRange(initial.GetProperty("monitors").EnumerateArray().Select(x=>(object)x.Clone()));Save();
            Check(initial.GetProperty("status").GetString()=="ready","actual Mutter monitor query",new{count=original.Length});
            Check(initial.GetProperty("inhibitorStatus").GetString()=="ready"&&initial.GetProperty("sleepInhibitorStatus").GetString()=="ready","independent GNOME/logind query availability");
            string reason="Personal Tools synthetic display-idle acceptance "+Guid.NewGuid().ToString("N");
            cookie=await manager.InhibitAsync("io.griplabs.PersonalTools.Acceptance",0,reason,8);
            var withIdle=await Read();
            Check(Contains(withIdle,"inhibitors",reason)&&!Contains(withIdle,"sleepInhibitors",reason),"GNOME idle inhibitor creation and separated display");
            await manager.UninhibitAsync(cookie.Value);cookie=null;
            Check(!Contains(await Read(),"inhibitors",reason),"GNOME idle inhibitor release");
            string sleepReason="Personal Tools synthetic sleep acceptance "+Guid.NewGuid().ToString("N");
            var info=new ProcessStartInfo("systemd-inhibit"){UseShellExecute=false,RedirectStandardError=true};
            foreach(var arg in new[]{"--what=sleep","--mode=block","--who=Personal Tools acceptance","--why="+sleepReason,"sleep","120"})info.ArgumentList.Add(arg);
            inhibitor=Process.Start(info)!;
            JsonElement withSleep=default;
            for(int i=0;i<30;i++){await Task.Delay(100);withSleep=await Read();if(Contains(withSleep,"sleepInhibitors",sleepReason)||inhibitor.HasExited)break;}
            if(inhibitor.HasExited)throw new IOException("systemd-inhibit: "+await inhibitor.StandardError.ReadToEndAsync());
            Check(Contains(withSleep,"sleepInhibitors",sleepReason)&&!Contains(withSleep,"inhibitors",sleepReason),"logind sleep inhibitor creation and separated display");
            inhibitor.Kill(true);await inhibitor.WaitForExitAsync();inhibitor.Dispose();inhibitor=null;
            for(int i=0;i<30;i++){withSleep=await Read();if(!Contains(withSleep,"sleepInhibitors",sleepReason))break;await Task.Delay(100);}
            Check(!Contains(withSleep,"sleepInhibitors",sleepReason),"logind sleep inhibitor release");
            if(exerciseHdr)
            {
                var initialLayout=await mutter.GetCurrentStateAsync();
                string layoutKey=JsonSerializer.Serialize(initialLayout.Logical.Select(x=>new{x.X,x.Y,x.Scale,x.Transform,x.Primary,connectors=x.Monitors.Select(m=>m.Connector).Order().ToArray()}));
                LinuxPaths.Write(Path.Combine(Path.GetDirectoryName(output)!,"hdr-restore.json"),new{savedAt=DateTimeOffset.UtcNow,monitors=original});
                foreach(var monitor in original.Where(x=>x.Supported))
                {
                    try{desktop.Set(monitor.Id,!monitor.Enabled);Check(desktop.Read().Single(x=>x.Id==monitor.Id).Enabled!=monitor.Enabled,"actual manual HDR toggle",new{monitor=monitor.Name});}
                    finally{desktop.Set(monitor.Id,monitor.Enabled);}
                    Check(desktop.Read().Single(x=>x.Id==monitor.Id).Enabled==monitor.Enabled,"manual HDR restores original",new{monitor=monitor.Name});
                }
                var primary=original.FirstOrDefault(x=>x.Primary&&x.Supported);
                if(primary is not null&&!primary.Enabled)
                {
                    string programDirectory=Path.Combine(isolated,"hdr-process");Directory.CreateDirectory(programDirectory);
                    // Ubuntu's multicall coreutils dispatches from argv[0]. Keep
                    // the basename while isolating the executable's full path.
                    string executable=Path.Combine(programDirectory,"sleep");
                    File.Copy("/usr/bin/sleep",executable,true);File.SetUnixFileMode(executable,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
                    await desktop.ExecuteAsync("save-rule",Json(new{name="Synthetic HDR acceptance",executable}));
                    await desktop.ExecuteAsync("auto-hdr",Json(new{enabled=true}));
                    Process Start(){var process=Process.Start(new ProcessStartInfo(executable,"120"){UseShellExecute=false})!;children.Add(process);return process;}
                    var first=Start();await desktop.RefreshAsync();Check(desktop.Read().Single(x=>x.Id==primary.Id).Enabled,"real process launch enables automatic HDR");
                    var second=Start();await desktop.RefreshAsync();first.Kill();await first.WaitForExitAsync();await desktop.RefreshAsync();Check(desktop.Read().Single(x=>x.Id==primary.Id).Enabled,"duplicate process exit retains HDR owner");
                    await desktop.ExecuteAsync("hdr",Json(new{id=primary.Id,enabled=false}));await desktop.RefreshAsync();Check(!desktop.Read().Single(x=>x.Id==primary.Id).Enabled,"manual HDR change wins while program remains active");
                    second.Kill();await second.WaitForExitAsync();await desktop.RefreshAsync();Check(!desktop.Read().Single(x=>x.Id==primary.Id).Enabled,"last program exit preserves manual override");
                    var third=Start();await desktop.RefreshAsync();Check(desktop.Read().Single(x=>x.Id==primary.Id).Enabled,"new program session reacquires HDR");
                    third.Kill();await third.WaitForExitAsync();
                    // Recreate the controller from its durable journal without invoking
                    // the old controller's shutdown, as after a daemon crash.
                    var recovered=new DesktopController(session);await recovered.RefreshAsync();
                    Check(!recovered.Read().Single(x=>x.Id==primary.Id).Enabled,"durable HDR journal restores after controller restart");
                    await recovered.ShutdownAsync();
                }
                var final=await Read();
                Check(original.All(m=>desktop.Read().Any(x=>x.Id==m.Id&&x.Enabled==m.Enabled)),"all HDR states restored");
                var finalMonitors=final.GetProperty("monitors").EnumerateArray().ToArray();
                Check(before.Cast<JsonElement>().All(m=>finalMonitors.Any(x=>x.GetProperty("id").GetString()==m.GetProperty("id").GetString()&&x.GetProperty("width").GetInt32()==m.GetProperty("width").GetInt32()&&x.GetProperty("height").GetInt32()==m.GetProperty("height").GetInt32()&&x.GetProperty("primary").GetBoolean()==m.GetProperty("primary").GetBoolean()&&Math.Abs(x.GetProperty("refreshRate").GetDouble()-m.GetProperty("refreshRate").GetDouble())<0.01)),"monitor resolution/refresh/primary preserved");
                var finalLayout=await mutter.GetCurrentStateAsync();
                Check(layoutKey==JsonSerializer.Serialize(finalLayout.Logical.Select(x=>new{x.X,x.Y,x.Scale,x.Transform,x.Primary,connectors=x.Monitors.Select(m=>m.Connector).Order().ToArray()})),"monitor positions/scales/transforms preserved");
            }
            completed=true;
        }
        catch(Exception error){failures.Add(error.Message);throw;}
        finally
        {
            if(cookie.HasValue)try{await manager.UninhibitAsync(cookie.Value);}catch(Exception e){failures.Add("GNOME cleanup: "+e.Message);}
            if(inhibitor is not null){if(!inhibitor.HasExited)inhibitor.Kill(true);inhibitor.Dispose();}
            foreach(var child in children){if(!child.HasExited)child.Kill();child.Dispose();}
            if(exerciseHdr)foreach(var monitor in original)try{if(desktop.Read().FirstOrDefault(x=>x.Id==monitor.Id)?.Enabled!=monitor.Enabled)desktop.Set(monitor.Id,monitor.Enabled);}catch(Exception e){failures.Add("HDR restore: "+e.Message);}
            Save();
        }
    }
}
