using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using PersonalTools;

namespace PersonalTools.HdrWidget;

[Guid("73CC5362-5F43-44EA-A82B-1DD4CF8796C5")]
internal sealed class WidgetProvider : IWidgetProvider
{
    private sealed class State { public int Page; public int PageSize = 2; public bool Active; public bool PowerWidget; public bool VllmWidget; }
    private readonly Dictionary<string, State> widgets = [];
    private readonly object sync = new();
    private readonly SemaphoreSlim updates = new(1);
    private readonly Timer timer;
    internal static readonly ManualResetEvent Empty = new(false);
    private DateTime lastWidget = DateTime.UtcNow;
    public WidgetProvider()
    {
        // The host can activate the COM server before its catalog is ready (or with no pinned widgets).
        // Creation/activation callbacks will populate the state even if startup enumeration is unavailable.
        try
        {
            foreach (var info in WidgetManager.GetDefault()?.GetWidgetInfos() ?? [])
            {
                var context = info.WidgetContext;
                widgets[context.Id] = new State { Page = int.TryParse(info.CustomState, out int p) ? Math.Max(0, p) : 0, PageSize = PageSize(context.Size), PowerWidget = WidgetCard.IsPowerWidget(context.DefinitionId), VllmWidget = context.DefinitionId == VllmCard.DefinitionId };
            }
        }
        catch (Exception error) { Report(error); }
        timer = new Timer(_ =>
        {
            lock (sync)
            {
                if (widgets.Count != 0) lastWidget = DateTime.UtcNow;
                else if (DateTime.UtcNow - lastWidget > TimeSpan.FromMinutes(1)) { Empty.Set(); return; }
            }
            QueueRefresh();
        }, null, 2000, 2000);
    }
    private static int PageSize(WidgetSize size) => WidgetCard.Capacity(size == WidgetSize.Large ? "large" : size == WidgetSize.Medium ? "medium" : "small");
    private void Track(WidgetContext context)
    {
        lock (sync)
        {
            if (!widgets.TryGetValue(context.Id, out var state)) widgets[context.Id] = state = new();
            state.PageSize = PageSize(context.Size); state.Active = true;
            state.PowerWidget = WidgetCard.IsPowerWidget(context.DefinitionId);
            state.VllmWidget = context.DefinitionId == VllmCard.DefinitionId;
        }
        QueueRefresh();
    }
    public void CreateWidget(WidgetContext context) => Track(context);
    public void Activate(WidgetContext context) => Track(context);
    public void OnWidgetContextChanged(WidgetContextChangedArgs args) => Track(args.WidgetContext);
    public void Deactivate(string id) { lock (sync) { if (widgets.TryGetValue(id, out var state)) state.Active = false; } }
    public void DeleteWidget(string id, string customState) { lock (sync) widgets.Remove(id); }
    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        // Callback arguments cannot be retained after this method returns.
        string id = args.WidgetContext.Id, verb = args.Verb, data = args.Data;
        HdrRequest request = new("read");
        string vllmCommand = "read";
        try
        {
            lock (sync)
            {
                if (!widgets.TryGetValue(id, out var state)) return;
                state.Active = true;
                if (state.VllmWidget)
                {
                    if (verb is not ("vllm-start" or "vllm-stop" or "refresh")) return;
                    vllmCommand = verb == "vllm-start" ? "start" : verb == "vllm-stop" ? "stop" : "refresh";
                }
                else if (verb == "page")
                {
                    using var json = JsonDocument.Parse(data);
                    state.Page = Math.Max(0, json.RootElement.GetProperty("page").GetInt32());
                }
                else if (verb == "set")
                {
                    if (state.PowerWidget) return;
                    using var json = JsonDocument.Parse(data);
                    request = new("set", json.RootElement.GetProperty("monitorId").GetString(), json.RootElement.GetProperty("enabled").GetBoolean());
                }
                else if (verb != "refresh") return;
            }
            _ = UpdateAsync(request, false, vllmCommand);
        }
        catch (Exception e) { Report(e); }
    }
    private void QueueRefresh() => _ = UpdateAsync(new("read"), true);
    private async Task UpdateAsync(HdrRequest request, bool skipBusy, string vllmCommand = "read")
    {
        if (skipBusy) { if (!await updates.WaitAsync(0)) return; }
        else await updates.WaitAsync();
        try
        {
            lock (sync) { if (request.Command != "set" && vllmCommand is not ("start" or "stop") && !widgets.Values.Any(s => s.Active)) return; }
            (string Id, int Page, int PageSize, bool PowerWidget, bool VllmWidget)[] targets;
            lock (sync) targets = widgets.Where(x => x.Value.Active).Select(x => (x.Key, x.Value.Page, x.Value.PageSize, x.Value.PowerWidget, x.Value.VllmWidget)).ToArray();
            HdrSnapshot snapshot = new([], [], null);
            if (targets.Any(x => !x.VllmWidget) || request.Command == "set")
            {
                try { snapshot = await WidgetClient.SendAsync(request); }
                catch (Exception e) { snapshot = new([], [], "앱 연결 실패 · 새로 고침으로 재시도하세요. " + e.Message); }
            }
            VllmSnapshot vllm = new();
            if (targets.Any(x => x.VllmWidget) || vllmCommand is "start" or "stop")
            {
                try { vllm = await WidgetClient.SendVllmAsync(vllmCommand); }
                catch (Exception e) { vllm = new("unavailable", "앱 연결 실패", Error: e.Message); }
            }
            foreach (var (id, page, pageSize, powerWidget, vllmWidget) in targets)
            {
                try
                {
                    WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(id)
                    {
                        Template = vllmWidget ? VllmCard.Create(vllm, pageSize >= 8) : powerWidget ? WidgetCard.CreatePower(snapshot, page, Math.Max(1, pageSize / 2)) : WidgetCard.Create(snapshot, page, pageSize), Data = "{}", CustomState = page.ToString()
                    });
                }
                catch (Exception e) { Report(e); }
            }
        }
        catch (Exception e) { Report(e); }
        finally { updates.Release(); }
    }
    internal static void Report(Exception e)
    {
        try { HdrFiles.Write("widget-error.json", new { Time = DateTimeOffset.Now, Error = e.ToString() }); } catch { }
    }
}
