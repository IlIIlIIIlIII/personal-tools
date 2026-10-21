using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tmds.DBus;
[assembly:SupportedOSPlatform("linux")]
namespace PersonalTools;
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if(!OperatingSystem.IsLinux()){Console.Error.WriteLine("Personal Tools Service requires Linux.");return 1;}
        // Protect all newly created archive/settings/log files, including portable routines.
        umask(0x3f);
        using var connection=new Connection(new ClientConnectionOptions(Address.Session){AutoConnect=false,RunContinuationsAsynchronously=true});
        try {
            await connection.ConnectAsync();
            await using var daemon=new Daemon(connection);
            bool snapshot=args.Contains("--snapshot");
            await daemon.InitializeAsync(!snapshot);
            if(snapshot){Console.WriteLine(await daemon.GetSnapshotAsync());return 0;}
            using var bus=new Tmds.DBus.Protocol.DBusConnection(Tmds.DBus.Protocol.DBusAddress.Session!);
            await bus.ConnectAsync();using var host=new BusHost(bus,daemon);await host.RegisterAsync();
            using var sigint=PosixSignalRegistration.Create(PosixSignal.SIGINT,ctx=>{ctx.Cancel=true;daemon.Quit.TrySetResult();});
            using var sigterm=PosixSignalRegistration.Create(PosixSignal.SIGTERM,ctx=>{ctx.Cancel=true;daemon.Quit.TrySetResult();});
            daemon.Log("service","info","Personal Tools Ubuntu 서비스가 시작되었습니다.");
            await daemon.Quit.Task;return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    [DllImport("libc")]static extern uint umask(uint mask);
}
