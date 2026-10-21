using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using PersonalTools.Bus;
using Tmds.DBus.Protocol;
namespace PersonalTools;
public sealed class BusHost : ServiceHandler, IPersonalTools1Handler, IDisposable
{
    readonly Daemon daemon;
    readonly List<IDisposable> subscriptions=[];
    sealed class ReplyScope {public List<SafeHandle> Handles {get;}=[];public bool Quit;}
    readonly AsyncLocal<ReplyScope?> replyScope=new();
    string registeredName="io.griplabs.PersonalTools";
    public BusHost(DBusConnection connection,Daemon daemon) : base(connection,"/io/griplabs/PersonalTools",false,false) {this.daemon=daemon;}
    public async Task RegisterAsync(string name="io.griplabs.PersonalTools")
    {
        Connection.AddMethodHandler(this);
        subscriptions.Add(await daemon.WatchChangedAsync(value=>Connection.EmitChanged(Path,value)));
        subscriptions.Add(await daemon.WatchClipboardChangedAsync(()=>Connection.EmitClipboardChanged(Path)));
        subscriptions.Add(await daemon.WatchEventAsync(value=>Connection.EmitEvent(Path,value)));
        await Connection.RequestNameAsync(name);
        registeredName=name;
    }
    protected override async ValueTask InvokeAsync(DBusMethod method,MethodContext context)
    {
        var scope=new ReplyScope();replyScope.Value=scope;
        try {
            await base.InvokeAsync(method,context);
            if(scope.Quit&&(context.ReplySent||context.NoReplyExpected)){
                // Reply() queues the response. A subsequent bus round trip proves
                // the bus has processed it before Program disposes the connection.
                try {await Connection.ReleaseNameAsync(registeredName);}
                finally {daemon.Quit.TrySetResult();}
            }
        }
        finally {foreach(var handle in scope.Handles)handle.Dispose();replyScope.Value=null;}
    }
    public async ValueTask<string> GetSnapshotAsync()=>await daemon.GetSnapshotAsync();
    public async ValueTask<string> ExecuteAsync(string domain,string action,string payload)
    {
        string response=await daemon.ExecuteAsync(domain,action,payload);
        if(domain=="lifecycle"&&action=="quit"){
            using var parsed=JsonDocument.Parse(response);
            if(parsed.RootElement.GetProperty("ok").GetBoolean())replyScope.Value!.Quit=true;
        }
        return response;
    }
    public async ValueTask<string> CaptureClipboardAsync(string metadata,SafeHandle[] payloadFds)
    {
        var owned=new List<SafeFileHandle>();
        try { foreach(var fd in payloadFds)owned.Add(new(NativeFile.Duplicate(fd),true));return await daemon.CaptureClipboardAsync(metadata,owned.ToArray()); }
        finally { foreach(var fd in owned)fd.Dispose();foreach(var fd in payloadFds)fd.Dispose(); }
    }
    public async ValueTask<(string Metadata,SafeHandle[] PayloadFds)> OpenClipboardAsync(string entryId)
    {
        var result=await daemon.OpenClipboardAsync(entryId);
        replyScope.Value!.Handles.AddRange(result.PayloadFds);
        return (result.Metadata,result.PayloadFds.Cast<SafeHandle>().ToArray());
    }
    public void Dispose(){foreach(var subscription in subscriptions)subscription.Dispose();}
}
