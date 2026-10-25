using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PersonalTools;

internal sealed class HdrController : IAsyncDisposable, ITaskbarHdrSource
{
    // Libraries change only when a game is installed or removed; process polling stays at 1 s.
    private static readonly TimeSpan GameRescan = TimeSpan.FromMinutes(5);
    private static readonly int Session = Process.GetCurrentProcess().SessionId;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly IHdrDisplay display;
    private readonly IHdrJournal journal;
    private readonly IHdrProgramStore programStore;
    private readonly IHdrSettingsStore settingsStore;
    private readonly string pipeName;
    private readonly Func<HdrGame[]> scanGames;
    private readonly HdrProcessList processList = new();
    // Process id → executable, so each process is opened once instead of every second.
    private readonly Dictionary<int, (string Name, HdrProcess? Item, DateTime Retry)> processCache = [];
    private HdrAutomation? automation;
    private List<HdrProgram> programs = [];
    private HdrSettings settings = new();
    private HdrGame[] games = [];
    private DateTimeOffset gamesDue;
    private DateTimeOffset? gamesScannedAt;
    private Task? worker, ticker, server;
    private string? startupError;
    private volatile bool closing;
    public HdrSnapshot Snapshot { get; private set; } = new([], [], null);
    public event Action<HdrSnapshot>? Changed;
    internal HdrController(IHdrDisplay? display = null, IHdrJournal? journal = null, IHdrProgramStore? programStore = null, string? pipeName = null,
        IHdrSettingsStore? settingsStore = null, Func<HdrGame[]>? scanGames = null)
    {
        this.display = display ?? new HdrDisplay();
        this.journal = journal ?? new HdrJournal();
        this.programStore = programStore ?? new HdrProgramStore();
        this.settingsStore = settingsStore ?? new HdrSettingsStore();
        this.pipeName = pipeName ?? HdrWire.PipeName;
        this.scanGames = scanGames ?? new HdrGameScanner().Scan;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, char[] name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    // Limited query rights also work for elevated games and most anti-cheat protected ones, unlike MainModule.
    // The key keeps the "<pid>:<UTC start ticks>" form of earlier builds so a journal they wrote still matches.
    internal static HdrProcess? Describe(int id)
    {
        using var process = OpenProcess(0x1000, false, id); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return null;
        var name = new char[1024];
        int size = name.Length;
        if (!QueryFullProcessImageName(process, 0, name, ref size) || !GetProcessTimes(process, out long created, out _, out _, out _)) return null;
        try { return new($"{id}:{DateTime.FromFileTimeUtc(created).Ticks}", Path.GetFullPath(new string(name, 0, size))); }
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
                    if (Describe(process.Id) is not { } item || item.Path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new(DisplayName(item.Path), item.Path, HdrGameScanner.Launchers.Contains(Path.GetFileName(item.Path))));
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
    // With detected games every process is a candidate (anything under a game folder counts), else only the
    // registered names. Other sessions' processes, such as services, never start a game for this user.
    private HdrProcess[] Scan(bool everyProcess)
    {
        var alive = new HashSet<int>();
        var result = new List<HdrProcess>();
        var names = programs.Where(p => p.Enabled).Select(p => Path.GetFileNameWithoutExtension(p.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Candidate(string name) => everyProcess || names.Contains(name);
        var now = DateTime.UtcNow;
        foreach (var (id, name, session, _) in processList.Read(Candidate))
        {
            if (session != Session || !Candidate(name)) continue;
            alive.Add(id);
            // A known process keeps its entry, so a temporary access failure cannot restore early.
            if (!processCache.TryGetValue(id, out var cached) || cached.Name != name || cached.Item is null && now >= cached.Retry)
                processCache[id] = cached = (name, Describe(id), now.AddSeconds(5));
            if (cached.Item is { } item) result.Add(item);
        }
        foreach (int pid in processCache.Keys.Where(pid => !alive.Contains(pid)).ToArray()) processCache.Remove(pid);
        return result.ToArray();
    }
    private void Refresh()
    {
        if (DateTimeOffset.Now >= gamesDue) RescanGames();
        var detected = settings.Games ? games : [];
        var matcher = new HdrMatcher(programs, detected, settings.DisabledGames);
        var processes = Scan(detected.Length > 0);
        var hits = processes.Select(p => (Process: p, Hit: matcher.Find(p.Path))).Where(x => x.Hit is not null).ToArray();
        // Switched off, automation matches nothing: pending sessions restore and nothing new starts.
        if (startupError is null) automation?.Reconcile(processes, settings.Enabled ? matcher : new HdrMatcher([]));
        string NameOf(string key)
        {
            var (process, hit) = hits.FirstOrDefault(x => x.Process.Key == key);
            return hit?.Game?.Name ?? hit?.Program?.Name ?? (process is null ? "프로그램" : Path.GetFileNameWithoutExtension(process.Path));
        }
        var active = automation?.Sessions.Select(s => new HdrActive(s.MonitorId, s.Original, s.Manual, s.StartedAt, s.Owners.Select(NameOf).Distinct().ToArray())).ToArray() ?? [];
        var off = (settings.DisabledGames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gameStatus = games.Select(game =>
        {
            var claim = HdrMatcher.Claim(game, programs);
            var running = hits.Where(x => x.Hit!.Game?.Key == game.Key).Select(x => x.Process).OrderBy(Started).ToArray();
            string? exe = running.Length == 0 ? null : Path.GetFileName(running[0].Path) + (running.Length > 1 ? $" 외 {running.Length - 1}개" : "");
            return new HdrGameStatus(game, claim?.Enabled ?? !off.Contains(game.Key), running.Length > 0, exe, claim?.Name);
        }).ToArray();
        // A program that claims a game also runs while only the game's launcher does.
        var programStatus = programs.Select(p => new HdrProgramStatus(p, hits.Any(x => x.Hit!.Program?.Id == p.Id
            || x.Hit.Game is { } game && HdrMatcher.Claim(game, programs)?.Id == p.Id))).ToArray();
        Publish(new(display.Read(), programStatus, startupError ?? automation?.Error, null, Enabled: settings.Enabled, Active: active,
            GamesEnabled: settings.Games, Games: gameStatus, GamesScannedAt: gamesScannedAt, TaskbarButtons: settings.TaskbarButtons, TaskbarHidden: settings.TaskbarHidden));
    }
    private static long Started(HdrProcess process) => long.TryParse(process.Key[(process.Key.IndexOf(':') + 1)..], out long ticks) ? ticks : 0;
    private void RescanGames()
    {
        // A failed scan keeps the previous list instead of dropping every game for five minutes. The invariant order
        // lists Latin names before Hangul like Windows' own app lists; ICU's Korean order puts Hangul first.
        try
        {
            games = scanGames().OrderBy(g => g.Name, StringComparer.InvariantCultureIgnoreCase).ToArray();
            gamesScannedAt = DateTimeOffset.Now;
        }
        catch { }
        gamesDue = DateTimeOffset.Now + GameRescan;
    }
    private void Publish(HdrSnapshot snapshot) { Snapshot = snapshot; Changed?.Invoke(Snapshot); }

    public Task SetEnabledAsync(bool enabled) => UpdateSettingsAsync(s => s with { Enabled = enabled });
    public Task SetGamesEnabledAsync(bool enabled) => UpdateSettingsAsync(s => s with { Games = enabled });
    public Task SetGameEnabledAsync(string key, bool enabled) => UpdateSettingsAsync(s =>
    {
        var off = (s.DisabledGames ?? []).Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        return s with { DisabledGames = (enabled ? off : off.Append(key)).ToArray() };
    });
    public Task SetTaskbarButtonsAsync(bool enabled) => UpdateSettingsAsync(s => s with { TaskbarButtons = enabled });
    public Task SetTaskbarMonitorAsync(string id, bool shown) => UpdateSettingsAsync(s =>
    {
        var hidden = (s.TaskbarHidden ?? []).Where(k => !string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
        return s with { TaskbarHidden = (shown ? hidden : hidden.Append(id)).ToArray() };
    });
    public async Task RescanGamesAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (closing) return;
            gamesDue = default;
            Refresh();
        }
        finally { gate.Release(); }
    }
    private async Task UpdateSettingsAsync(Func<HdrSettings, HdrSettings> change)
    {
        await gate.WaitAsync();
        try
        {
            if (closing) return;
            if (automation is null) throw new IOException("HDR 서비스를 시작하고 있습니다. 다시 시도하세요.");
            if (startupError is not null) throw new IOException(startupError);
            var next = change(settings);
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
