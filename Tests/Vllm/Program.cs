using System.IO.Pipes;
using System.Text.Json;
using PersonalTools;
using PersonalTools.HdrWidget;

static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
static async Task WaitUntil(Func<bool> ready)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!ready()) await Task.Delay(10, timeout.Token);
}
static async Task<VllmSnapshot> Send(string command, string? name = null)
{
    using var pipe = new NamedPipeClientStream(".", name ?? VllmProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await pipe.ConnectAsync(timeout.Token);
    await HdrWire.WriteAsync(pipe, command, timeout.Token);
    return await HdrWire.ReadAsync<VllmSnapshot>(pipe, timeout.Token);
}
if (args.Contains("--live-cycle"))
{
    Check((await Send("refresh")).CanStop, "live cycle can stop or confirm stopped service");
    var stopping = await Send("stop");
    Check(stopping.State == "stopping", "installed app accepted stop");
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    VllmSnapshot state;
    do { await Task.Delay(500, deadline.Token); state = await Send("read"); } while (state.Busy);
    Console.WriteLine(JsonSerializer.Serialize(state));
    Check(state.State == "stopped" && state.Error is null, "actual service and API stopped");
    Check((await Send("start")).State == "starting", "restart accepted after stop");
    string last = "";
    do
    {
        await Task.Delay(1000, deadline.Token); state = await Send("read");
        string detail = state.Progress?.Detail ?? state.Message;
        if (detail != last) { Console.WriteLine(detail); last = detail; }
    } while (state.Busy);
    Check(state.Ready && state.Error is null, "actual model restarted and warmup completed");
    return;
}
if (args.Contains("--live-app"))
{
    var state = await Send("refresh");
    Console.WriteLine(JsonSerializer.Serialize(state));
    Check(state.Ready && state.Context == 65536, "installed app IPC verifies expected live model and context");
    var again = await Send("start");
    Check(again.Ready && !again.Busy, "already-running start is idempotent");
    return;
}
var expected = VllmProtocol.ParseModels("{\"data\":[{\"id\":\"qwen3.8-27b\",\"max_model_len\":65536}]}");
static string Event(string source, string line) => JsonSerializer.Serialize(new { source, line });
var progress = VllmProgress.Begin();
progress = progress.Observe(Event("bridge", "locked"));
Check(progress.Step == 1, "service stage follows WSL lock acquisition");
progress = progress.Observe(Event("log", "Loading safetensors checkpoint shards:  40% Completed | 2/5 [00:02]"));
Check(progress.Step == 2 && progress.Percent == 40 && progress.Detail.Contains("2/5"), "real shard progress is parsed");
progress = progress.Observe(Event("log", "Loading drafter model..."));
Check(progress.Percent is null && progress.Detail.Contains("MTP"), "draft model resets substage percentage");
progress = progress.Observe(Event("log", "Model loading took 19.27 GiB memory and 10.8 seconds"));
Check(progress.Step == 3, "engine preparation follows weight loading");
progress = progress.Observe(Event("log", "Capturing CUDA graphs (decode, FULL): 100%|done|"));
Check(progress.Step == 3 && progress.Percent == 100, "graph percentage is substage only");
progress = progress.Observe(Event("log", "Application startup complete."));
Check(progress.Step == 4 && progress.Percent is null, "API startup is not warmup completion");
progress = progress.Observe(Event("stdout", "Ready: http://127.0.0.1:8000/v1 | model: qwen3.8-27b"));
Check(progress.Step == 5 && progress.Detail.Contains("1/2"), "readiness starts first warmup");
progress = progress.Observe(Event("stdout", "Chat warmup 1/2: 12.3s"));
Check(progress.Step == 5 && progress.Percent == 50, "first warmup advances to second");
Check(progress.Observe(Event("log", "Starting to load model stale...")) == progress, "late server log cannot regress progress");
progress = progress.Observe(Event("stdout", "Chat warmup 2/2: 3.2s"));
Check(progress.Step == 6, "second warmup still requires final Windows API check");
Check(progress.Observe("not-json") == progress, "unrecognized output cannot invent a stage");
using (var card = JsonDocument.Parse(VllmCard.Create(new("starting", progress.Title, Progress: progress), true)))
    Check(card.RootElement.GetRawText().Contains(JsonSerializer.Serialize("대화 워밍업")), "large widget includes stage checklist");
Check(expected.Ready && expected.Context == 65536, "model identity and live context");
Check(!VllmProtocol.ParseModels("{\"data\":[{\"id\":\"another-model\"}]}").CanStart, "different model blocks start");
int launches = 0, probes = 0;
bool online = false;
var warmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var controller = new VllmController(
    () => { Interlocked.Increment(ref probes); return Task.FromResult(online ? expected : new VllmSnapshot("offline", "offline")); },
    async token => { Interlocked.Increment(ref launches); online = true; await warmup.Task.WaitAsync(token); }))
{
    var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => controller.RequestAsync("start")));
    await WaitUntil(() => launches == 1);
    Check(results.All(x => x.Busy) && launches == 1, "concurrent starts invoke launcher once");
    Check((await controller.RequestAsync("refresh")).Busy, "API online still starting until warmup completes");
    warmup.SetResult();
    await WaitUntil(() => controller.Snapshot.Ready);
    Check((await controller.RequestAsync("start")).Ready && launches == 1, "ready model is not restarted");
    int prior = probes;
    await controller.RequestAsync("read"); await controller.RequestAsync("read");
    Check(probes == prior, "repeated widget polling uses cached status");
}
string testPipe = "PersonalTools.Vllm.Test." + Guid.NewGuid().ToString("N");
int stops = 0;
bool stopOnline = true;
var stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var controller = new VllmController(
    () => Task.FromResult(stopOnline ? expected : new VllmSnapshot("offline", "offline")),
    stopService: async token => { Interlocked.Increment(ref stops); await stopCompletion.Task.WaitAsync(token); stopOnline = false; }))
{
    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => controller.RequestAsync("stop")));
    await WaitUntil(() => stops == 1);
    Check(results.All(x => x.State == "stopping" && !x.CanStart && !x.CanStop), "concurrent stops launch once and disable controls");
    Check((await controller.RequestAsync("start")).State == "stopping", "start cannot race shutdown");
    stopCompletion.SetResult();
    await WaitUntil(() => controller.Snapshot.State == "stopped");
    Check((await controller.RequestAsync("refresh")).State == "stopped", "confirmed stop survives polling");
    Check(controller.Snapshot.CanStart && !controller.Snapshot.CanStop, "stopped server can restart");
}
await using (var controller = new VllmController(() => Task.FromResult(expected), stopService: _ => throw new IOException("stop failed")))
{
    await controller.RequestAsync("stop");
    await WaitUntil(() => controller.Snapshot.Error is not null);
    Check(controller.Snapshot.State != "stopped", "failed stop never reports success");
}
foreach (var state in new[] { expected, new VllmSnapshot("stopping", "중지 중"), new VllmSnapshot("stopped", "중지됨"), new VllmSnapshot("conflict", "다른 모델") })
{
    using var card = JsonDocument.Parse(VllmCard.Create(state));
    bool hasStop = card.RootElement.GetProperty("actions").EnumerateArray().Any(x => x.GetProperty("verb").GetString() == "vllm-stop");
    Check(hasStop == state.CanStop, "widget stop availability: " + state.State);
}
await using (var controller = new VllmController(() => Task.FromResult(new VllmSnapshot("offline", "offline")), _ => throw new IOException("warmup failed"), testPipe))
{
    controller.Start();
    Check((await Send("start", testPipe)).Busy, "pipe start returns promptly");
    await WaitUntil(() => controller.Snapshot.Error is not null);
    Check((await Send("refresh", testPipe)).Error!.Contains("warmup failed"), "failure survives offline refresh");
    Check((await Send("invalid", testPipe)).Error!.Contains("지원하지"), "pipe rejects unknown command");
}
foreach (var state in new[] { expected, new VllmSnapshot("starting", "워밍업 중"), new VllmSnapshot("conflict", "다른 모델"), new VllmSnapshot("offline", "연결 안 됨") })
{
    using var card = JsonDocument.Parse(VllmCard.Create(state));
    bool start = card.RootElement.GetProperty("actions").EnumerateArray().Any(x => x.GetProperty("verb").GetString() == "vllm-start");
    Check(start == state.CanStart, "widget start availability: " + state.State);
}
