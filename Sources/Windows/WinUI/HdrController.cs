using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32;

namespace PersonalTools;

internal sealed class HdrController : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly IHdrDisplay display;
    private readonly IHdrJournal journal;
    private readonly IHdrProgramStore programStore;
    private readonly string pipeName;
    private readonly List<ManagementEventWatcher> watchers = [];
    private readonly Dictionary<int, HdrProcess> processCache = [];
    private HdrAutomation? automation;
    private List<HdrProgram> programs = [];
    private Task? worker, ticker, server;
    private string? startupError, watcherError;
    private volatile bool closing;
    private readonly DisplayPowerMonitor displayPower = new();
    public void StartDisplayPower() => displayPower.Start();
    public HdrSnapshot Snapshot { get; private set; } = new([], [], null);
    public event Action<HdrSnapshot>? Changed;
    internal HdrController(IHdrDisplay? display = null, IHdrJournal? journal = null, IHdrProgramStore? programStore = null, string? pipeName = null)
    {
        this.display = display ?? new HdrDisplay();
        this.journal = journal ?? new HdrJournal();
        this.programStore = programStore ?? new HdrProgramStore();
        this.pipeName = pipeName ?? HdrWire.PipeName;
    }
    public static void RegisterWidgetApp()
    {
        using var key = Registry.CurrentUser.CreateSubKey(HdrWire.RegistryPath);
        key.SetValue("AppPath", Environment.ProcessPath!);
    }

    public void Start()
    {
        if (worker is not null) return;
        displayPower.StartAutomatic();
        worker = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                programs = programStore.Load();
                automation = new(display, journal);
                foreach (var (trace, intrinsic) in new[] { ("Win32_ProcessStartTrace", "__InstanceCreationEvent"), ("Win32_ProcessStopTrace", "__InstanceDeletionEvent") })
                {
                    try { Watch("SELECT * FROM " + trace); }
                    catch
                    {
                        // Trace providers can require elevation. Intrinsic events work as a normal user.
                        try { Watch($"SELECT * FROM {intrinsic} WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'"); }
                        catch (Exception e) { watcherError = "프로세스 이벤트 연결 실패 · 2초 간격으로 확인합니다. " + e.Message; }
                    }
                }
            }
            catch (Exception e) { startupError = "HDR 자동 제어 시작 실패: " + e.Message; }
            finally { gate.Release(); }
            signals.Writer.TryWrite(true);
            try
            {
                await foreach (var _ in signals.Reader.ReadAllAsync(stop.Token))
                {
                    await gate.WaitAsync(stop.Token);
                    try { Refresh(); }
                    catch (Exception e) { Publish(Snapshot with { Error = e.Message }); }
                    finally { gate.Release(); }
                }
            }
            catch (OperationCanceledException) { }
        });
        ticker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try { while (await timer.WaitForNextTickAsync(stop.Token)) signals.Writer.TryWrite(true); }
            catch (OperationCanceledException) { }
        });
        server = Task.Run(ServeAsync);
    }

    private void Watch(string query)
    {
        var watcher = new ManagementEventWatcher(new WqlEventQuery(query));
        watcher.EventArrived += (_, _) => signals.Writer.TryWrite(true);
        try { watcher.Start(); watchers.Add(watcher); }
        catch { watcher.Dispose(); throw; }
    }

    internal static HdrProcess? Describe(Process process)
    {
        try
        {
            string? path = process.MainModule?.FileName;
            return path is null ? null : new($"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}", Path.GetFullPath(path));
        }
        catch { return null; }
    }
    public static HdrProcess[] ListProcesses()
    {
        var result = new List<HdrProcess>();
        foreach (var process in Process.GetProcesses())
            using (process) { if (Describe(process) is { } item) result.Add(item); }
        return result.DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Path).ToArray();
    }
    private HdrProcess[] Scan()
    {
        var alive = new HashSet<int>();
        var names = programs.Where(p => p.Enabled).Select(p => Path.GetFileNameWithoutExtension(p.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                alive.Add(process.Id);
                try
                {
                    if (!names.Contains(process.ProcessName)) { processCache.Remove(process.Id); continue; }
                    var item = Describe(process);
                    if (item is not null) processCache[process.Id] = item;
                    // Keep a known live process on a temporary access failure, avoiding early restore.
                }
                catch (InvalidOperationException) { }
            }
        }
        foreach (int pid in processCache.Keys.Where(pid => !alive.Contains(pid)).ToArray()) processCache.Remove(pid);
        return processCache.Values.ToArray();
    }
    private void Refresh()
    {
        var processes = Scan();
        if (startupError is null) automation?.Reconcile(processes, programs);
        Publish(new(display.Read(), programs.Select(p => new HdrProgramStatus(p, processes.Any(x => string.Equals(x.Path, p.Path, StringComparison.OrdinalIgnoreCase)))).ToArray(), startupError ?? automation?.Error, watcherError));
    }
    private void Publish(HdrSnapshot snapshot) { Snapshot = snapshot with { DisplayPower = displayPower.Snapshot }; Changed?.Invoke(Snapshot); }

    public async Task SaveProgramAsync(HdrProgram program, bool remove = false)
    {
        await gate.WaitAsync();
        try
        {
            if (closing) return;
            if (automation is null) throw new IOException("HDR 서비스를 시작하고 있습니다. 다시 시도하세요.");
            if (startupError is not null) throw new IOException(startupError);
            var next = programs.Where(p => p.Id != program.Id).ToList();
            if (!remove)
            {
                var path = Path.GetFullPath(program.Path);
                if (!Path.IsPathFullyQualified(program.Path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("실행 파일의 전체 경로를 선택하세요.");
                if (string.IsNullOrWhiteSpace(program.Name)) throw new IOException("표시 이름을 입력하세요.");
                if (next.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) throw new IOException("이미 등록된 실행 파일입니다.");
                next.Add(program with { Path = path, Name = program.Name.Trim() });
            }
            programStore.Save(next);
            programs = next;
            Refresh();
        }
        finally { gate.Release(); }
    }
    public async Task<HdrSnapshot> RequestAsync(HdrRequest request)
    {
        await gate.WaitAsync();
        try
        {
            if (closing) throw new IOException("앱이 종료 중입니다.");
            if (request.Command == "set")
            {
                if (automation is null || startupError is not null) throw new IOException(startupError ?? "HDR 서비스를 시작하고 있습니다. 다시 시도하세요.");
                automation.SetManual(request.MonitorId ?? "", request.Enabled);
            }
            else if (request.Command != "read") throw new IOException("지원하지 않는 HDR 명령입니다.");
            Refresh();
            return Snapshot;
        }
        catch (Exception error)
        {
            try { Publish(Snapshot with { Monitors = display.Read(), Error = error.Message }); }
            catch { Publish(Snapshot with { Error = error.Message }); }
            throw;
        }
        finally { gate.Release(); }
    }
    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                HdrSnapshot response;
                try
                {
                    var request = await HdrWire.ReadAsync<HdrRequest>(pipe, deadline.Token);
                    response = await RequestAsync(request);
                }
                catch (Exception e) { response = Snapshot with { Error = e.Message }; }
                await HdrWire.WriteAsync(pipe, response, deadline.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Publish(Snapshot with { Error = "위젯 연결 오류: " + e.Message });
                try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        closing = true;
        stop.Cancel();
        await displayPower.DisposeAsync();
        await Task.WhenAll(new[] { worker, ticker, server }.OfType<Task>());
        foreach (var watcher in watchers) { try { watcher.Stop(); } catch { } watcher.Dispose(); }
        await gate.WaitAsync();
        try { automation?.Shutdown(); }
        finally { gate.Release(); stop.Dispose(); }
    }
}
