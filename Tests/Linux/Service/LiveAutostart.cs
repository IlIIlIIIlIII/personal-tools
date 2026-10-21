using System.Text.Json;
using PersonalTools;
using Tmds.DBus;

internal static class LiveAutostart
{
    public static async Task RunAsync()
    {
        var before=await ProcessRunner.RunAsync("systemctl",["--user","is-enabled","personal-tools.service"]);
        string original=before.Output.Trim();
        if(original is not("enabled" or "disabled"))throw new IOException("Live autostart test requires a persistent enabled/disabled unit state.");
        string activeBefore=(await ProcessRunner.RunAsync("systemctl",["--user","is-active","personal-tools.service"])).Output.Trim();
        var checks=new List<object>();var failures=new List<string>();
        void Check(bool value,string name){checks.Add(new{name,passed=value});if(!value)throw new IOException(name);Console.WriteLine("PASS "+name);}
        using var session=new Connection(new ClientConnectionOptions(Address.Session){RunContinuationsAsynchronously=true});await session.ConnectAsync();
        await using var daemon=new Daemon(session);await daemon.InitializeAsync(false);
        async Task<bool> Snapshot(){await daemon.ExecuteAsync("diagnostics","refresh","{}");using var value=JsonDocument.Parse(await daemon.GetSnapshotAsync());return value.RootElement.GetProperty("settings").GetProperty("autostart").GetBoolean();}
        async Task Set(bool enabled){using var result=JsonDocument.Parse(await daemon.ExecuteAsync("settings","update",JsonSerializer.Serialize(new{autostart=enabled})));if(!result.RootElement.GetProperty("ok").GetBoolean())throw new IOException(result.RootElement.GetProperty("error").ToString());}
        try
        {
            Check(await Snapshot()==(original=="enabled"),"snapshot follows original systemd autostart state");
            var enable=await ProcessRunner.RunAsync("systemctl",["--user","enable","personal-tools.service"]);
            if(enable.ExitCode!=0)throw new IOException(enable.Error);
            Check(await Snapshot(),"external CLI enable is reflected in refreshed snapshot");
            await Set(false);
            Check((await ProcessRunner.RunAsync("systemctl",["--user","is-enabled","personal-tools.service"])).Output.Trim()=="disabled"&&!await Snapshot(),"settings toggle disables actual previously CLI-enabled unit");
            await Set(true);
            Check((await ProcessRunner.RunAsync("systemctl",["--user","is-enabled","personal-tools.service"])).Output.Trim()=="enabled"&&await Snapshot(),"settings toggle enables actual unit and snapshot");
            Check((await ProcessRunner.RunAsync("systemctl",["--user","is-active","personal-tools.service"])).Output.Trim()==activeBefore,"autostart changes never start or stop the running service");
        }
        catch(Exception error){failures.Add(error.Message);throw;}
        finally
        {
            try{await Set(original=="enabled");}
            catch(Exception error){failures.Add("settings restore: "+error.Message);await ProcessRunner.RunAsync("systemctl",["--user",original=="enabled"?"enable":"disable","personal-tools.service"]);}
            string final=(await ProcessRunner.RunAsync("systemctl",["--user","is-enabled","personal-tools.service"])).Output.Trim();
            if(final!=original)failures.Add("Original systemd autostart state was not restored");
            LinuxPaths.Write(Path.GetFullPath("build/linux/autostart-live.json"),new{observedAt=DateTimeOffset.UtcNow,checks,failures,originalState=original,finalState=final,serviceStateBefore=activeBefore,passed=failures.Count==0});
        }
    }
}
