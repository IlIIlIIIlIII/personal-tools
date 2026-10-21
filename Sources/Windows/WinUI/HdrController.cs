using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32;

namespace PersonalTools;

internal sealed class HdrController : IAsyncDisposable
{
    // Launchers stay open for the whole session, so registering one would keep HDR on all day.
    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe", "Battle.net.exe", "EADesktop.exe", "Origin.exe",
        "GalaxyClient.exe", "upc.exe", "UbisoftConnect.exe", "RiotClientServices.exe", "RiotClientUx.exe", "XboxPcApp.exe",
        "Playnite.DesktopApp.exe", "Playnite.FullscreenApp.exe",
    };
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly IHdrDisplay display;
    private readonly IHdrJournal journal;
    private readonly IHdrProgramStore programStore;
    private readonly IHdrSettingsStore settingsStore;
    private readonly string pipeName;
    private readonly Dictionary<int, HdrProcess> processCache = [];
    private HdrAutomation? automation;
    private List<HdrProgram> programs = [];
    private HdrSettings settings = new();
    private Task? worker, ticker, server;
    private string? startupError;
    private volatile bool closing;
    public HdrSnapshot Snapshot { get; private set; } = new([], [], null);
    public event Action<HdrSnapshot>? Changed;
    internal HdrController(IHdrDisplay? display = null, IHdrJournal? journal = null, IHdrProgramStore? programStore = null, string? pipeName = null,
        IHdrSettingsStore? settingsStore = null)
    {
        this.display = display ?? new HdrDisplay();
        this.journal = journal ?? new HdrJournal();
        this.programStore = programStore ?? new HdrProgramStore();
        this.settingsStore = settingsStore ?? new HdrSettingsStore();
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
        worker = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                settings = settingsStore.Load();
                programs = programStore.Load();
                automation = new(display, journal);
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
        // Polls the process list once a second. Process trace events need elevation, and the
        // normal-user WMI fallback (__InstanceCreationEvent WITHIN 1) re-reads every Win32_Process each
        // second: on this PC it raised WmiPrvSE from about 9% to 49% of a core (2026-10-21).
        ticker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try { while (await timer.WaitForNextTickAsync(stop.Token)) signals.Writer.TryWrite(true); }
            catch (OperationCanceledException) { }
        });
        server = Task.Run(ServeAsync);
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
    // Programs with a window (games, players), not background services or Windows itself.
    public static HdrCandidate[] ListCandidates()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        var result = new List<HdrCandidate>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    if (Describe(process) is not { } item || item.Path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new(DisplayName(item.Path), item.Path, Launchers.Contains(Path.GetFileName(item.Path))));
                }
                catch { }
            }
        }
        return result.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static string DisplayName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var name in new[] { info.FileDescription, info.ProductName })
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
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
        // Switched off, automation sees no programs: pending sessions restore and nothing new starts.
        if (startupError is null) automation?.Reconcile(processes, settings.Enabled ? programs : []);
        string NameOf(string key)
        {
            string? path = processes.FirstOrDefault(p => p.Key == key)?.Path;
            return programs.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))?.Name
                ?? (path is null ? "프로그램" : Path.GetFileNameWithoutExtension(path));
        }
        var active = automation?.Sessions.Select(s => new HdrActive(s.MonitorId, s.Original, s.Manual, s.StartedAt, s.Owners.Select(NameOf).Distinct().ToArray())).ToArray() ?? [];
        Publish(new(display.Read(), programs.Select(p => new HdrProgramStatus(p, processes.Any(x => string.Equals(x.Path, p.Path, StringComparison.OrdinalIgnoreCase)))).ToArray(),
            startupError ?? automation?.Error, null, Enabled: settings.Enabled, Active: active));
    }
    private void Publish(HdrSnapshot snapshot) { Snapshot = snapshot; Changed?.Invoke(Snapshot); }

    public async Task SetEnabledAsync(bool enabled)
    {
        await gate.WaitAsync();
        try
        {
            if (closing) return;
            if (automation is null) throw new IOException("HDR 서비스를 시작하고 있습니다. 다시 시도하세요.");
            if (startupError is not null) throw new IOException(startupError);
            var next = settings with { Enabled = enabled };
            settingsStore.Save(next);
            settings = next;
            Refresh();
        }
        finally { gate.Release(); }
    }
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
    // Lets the HDR widget and the live tests drive the running app; current user and session only.
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
        await Task.WhenAll(new[] { worker, ticker, server }.OfType<Task>());
        await gate.WaitAsync();
        try { automation?.Shutdown(); }
        finally { gate.Release(); stop.Dispose(); }
    }
}
