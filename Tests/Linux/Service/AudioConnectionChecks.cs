using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PersonalTools;

internal static class AudioConnectionChecks
{
    // Exercise actual AF_UNIX connect semantics in the suite's private XDG
    // runtime; fake device observation ensures these tests never route audio.
    sealed class Routes : IAudioRouteBackend
    {
        public Task<AudioRouteObservation> ReadAsync(CancellationToken token)=>Task.FromResult(new AudioRouteObservation(new(),new(),new()));
        public Task SetConfiguredAsync(string kind,string? name,CancellationToken token)=>throw new Exception("Connection tests must not change devices.");
    }
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,LinuxPaths.Json);
    public static async Task RunAsync(Action<bool,string> assert)
    {
        string path=Path.Combine(LinuxPaths.Runtime,"audio.sock"),settings=Path.Combine(LinuxPaths.Config,"audio.json");
        var original=File.Exists(settings)?File.ReadAllText(settings):null;
        var audio=new AudioClient(new AudioRouting(new Routes(),Path.Combine(LinuxPaths.State,"connection-test-routes.json")));
        try
        {
            // The same .NET error text as the user's screenshot is produced
            // by an absent Unix path, not an IP/network adapter failure.
            try{using var probe=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);await probe.ConnectAsync(new UnixDomainSocketEndPoint(path));throw new Exception("Missing socket connected.");}
            catch(SocketException e){assert(e.SocketErrorCode==SocketError.AddressNotAvailable,"missing AF_UNIX path reproduces .NET AddressNotAvailable");}
            var filter=new[]{new{frequency=66.0,gain=-12.0,q=7.4,enabled=true}};
            LinuxPaths.Write(settings,new{eqEnabled=true,aecEnabled=true,leftFilters=filter,rightFilters=filter,leftFile="left.txt",rightFile="right.txt"});
            string before=File.ReadAllText(settings);
            await audio.RefreshAsync(restore:false);
            var unavailable=Json(audio.Snapshot);
            assert(unavailable.GetProperty("status").GetString()=="unavailable"&&!unavailable.GetProperty("eqEnabled").GetBoolean()&&!unavailable.GetProperty("aecEnabled").GetBoolean(),"unavailable engine never claims EQ or NPU processing is active");
            assert(unavailable.GetProperty("leftFilters").GetArrayLength()==1&&unavailable.GetProperty("rightFilters").GetArrayLength()==1&&unavailable.GetProperty("leftFile").GetString()=="left.txt"&&File.ReadAllText(settings)==before,"temporary engine failure preserves saved EQ curves and files without writes");
            assert(unavailable.GetProperty("error").GetString()!.Contains("오디오 엔진에 연결할 수 없습니다"),"missing socket reports actionable audio connection failure");

            var lateServer=Task.Run(async()=>
            {
                await Task.Delay(250);
                using var listener=Listen(path);
                using var socket=await listener.AcceptAsync();using var stream=new NetworkStream(socket);using var reader=new StreamReader(stream);
                var request=JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id=request.GetProperty("id").GetString(),ok=true,data=new{status="ready"}})+"\n"));
            });
            var status=await audio.SendAsync("status",Json(new{}));await lateServer.WaitAsync(TimeSpan.FromSeconds(5));
            assert(status.GetProperty("status").GetString()=="ready","connect waits for delayed engine startup and sends one request");
            // Disposed listener leaves its pathname behind: retry refused
            // connections with the same bounded policy, not only ENOENT.
            var watch=Stopwatch.StartNew();
            try{await audio.SendAsync("status",Json(new{}));throw new Exception("Stale socket connected.");}
            catch(IOException e){assert(e.InnerException is SocketException&&watch.Elapsed<TimeSpan.FromSeconds(4),"stale engine socket reports a bounded connection failure");}
            File.Delete(path);
            using(var cancel=new CancellationTokenSource(50))
            {
                watch.Restart();
                try{await audio.SendAsync("status",Json(new{}),cancel.Token);throw new Exception("Cancelled connect succeeded.");}
                catch(OperationCanceledException){assert(watch.Elapsed<TimeSpan.FromSeconds(1),"connection retry respects immediate cancellation");}
            }
            using(var listener=Listen(path))
            {
                var dropped=Task.Run(async()=>
                {
                    using var socket=await listener.AcceptAsync();using var stream=new NetworkStream(socket);using var reader=new StreamReader(stream);
                    var request=JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement;
                    return request.GetProperty("command").GetString();
                });
                try{await audio.SendAsync("configure",Json(new{eqEnabled=true}));throw new Exception("Dropped reply succeeded.");}
                catch(IOException e){assert(e.Message.Contains("연결이 끊어졌습니다")&&await dropped=="configure"&&!listener.Poll(100_000,SelectMode.SelectRead),"disconnect after command write fails explicitly without replaying mutation");}
            }
            File.Delete(path);
            using(var listener=Listen(path))
            {
                var restored=new List<string>();
                var server=Task.Run(async()=>
                {
                    for(int i=0;i<4;i++)
                    {
                        using var socket=await listener.AcceptAsync();using var stream=new NetworkStream(socket);using var reader=new StreamReader(stream);
                        var request=JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement;restored.Add(request.GetProperty("command").GetString()!);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id=request.GetProperty("id").GetString(),ok=true,data=new{status="ready",eqEnabled=false,aecEnabled=false,leftFilters=filter,rightFilters=filter}})+"\n"));
                    }
                });
                // This recovery is EQ-only: no NPU fixture installation needed.
                LinuxPaths.Write(settings,new{eqEnabled=false,aecEnabled=false,leftFilters=filter,rightFilters=filter,leftFile="left.txt",rightFile="right.txt"});
                await audio.RefreshAsync();await audio.RefreshAsync();await server.WaitAsync(TimeSpan.FromSeconds(5));
                assert(restored.SequenceEqual(new[]{"mute","restore","status","status"})&&Json(audio.Snapshot).GetProperty("status").GetString()=="ready","engine recovery replays saved filters once and clears unavailable state");
            }
        }
        finally{File.Delete(path);if(original is null)File.Delete(settings);else File.WriteAllText(settings,original);}
    }
    static Socket Listen(string path)
    {
        var listener=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));listener.Listen(8);return listener;
    }
}
