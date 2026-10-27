using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalTools;

// Monitors holds friendly names (every monitor of that model) or device paths; null means MO32U24.
// Enabled is not part of settings.json: DisplayIdleFiles keeps it for the current sign-in only.
public sealed record DisplayIdleSettings(int IdleSeconds = 60, string[]? Monitors = null)
{
    public static readonly string[] DefaultMonitors = ["MO32U24"];
    [JsonIgnore] public bool Enabled { get; init; }
    [JsonIgnore] public IReadOnlyCollection<string> Selectors => Monitors ?? DefaultMonitors;
    [JsonIgnore] public long IdleMilliseconds => Math.Max(IdleSeconds, 10) * 1000L;
    internal bool Selects(DisplayInfo monitor) => !monitor.Internal && Selectors.Any(s =>
        string.Equals(s, monitor.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(s, monitor.Id, StringComparison.OrdinalIgnoreCase));
}

// What the page shows. Responding stays null until the monitor has been asked over DDC/CI.
public sealed record DisplayIdleMonitor(string Id, string Name, string Detail, bool Supported, bool Selected, bool? Responding, bool Dark);
public sealed record DisplayIdleEvent(DateTime At, string Text, string? Note = null);
public sealed record DisplayIdleSnapshot(DisplayIdleSettings Settings, DisplayIdleMonitor[] Monitors, DisplayIdleEvent[] Recent,
    bool Dark = false, DateTime? DarkSince = null, int DarkCount = 0, string? Error = null);

// settings.json, dark.json (monitors this app turned off, so a restart can turn them back on) and
// log.txt under %LOCALAPPDATA%\PersonalTools\DisplayIdle. The on switch is a volatile key under HKCU
// instead: restarting Windows or signing out removes it, so the feature starts off until someone
// turns it on by hand, while an app restart in the same sign-in (the task's restart, an update)
// keeps it (the user's choice, 2026-10-26).
internal sealed class DisplayIdleFiles(string? folder = null, string onKey = @"Software\PersonalTools\DisplayIdleOn")
{
    private const long LogLimit = 256 * 1024;
    public string Folder { get; } = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalTools", "DisplayIdle");
    public string OnKey { get; } = onKey;
    public string SettingsPath => Path.Combine(Folder, "settings.json");
    private string DarkPath => Path.Combine(Folder, "dark.json");
    public string LogPath => Path.Combine(Folder, "log.txt");

    public DateTime SettingsStamp => File.GetLastWriteTimeUtc(SettingsPath);
    public DisplayIdleSettings LoadSettings() => (File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<DisplayIdleSettings>(File.ReadAllText(SettingsPath)) ?? new() : new()) with { Enabled = IsOn };
    public void SaveSettings(DisplayIdleSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings));
        File.Move(temp, SettingsPath, true);
        SetOn(settings.Enabled);
    }
    private bool IsOn
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(OnKey); return key is not null; }
    }
    private void SetOn(bool on)
    {
        if (!on) { Registry.CurrentUser.DeleteSubKey(OnKey, false); return; }
        // The parent is created first so it stays a normal key; only the marker is volatile.
        int split = OnKey.LastIndexOf('\\');
        using var parent = Registry.CurrentUser.CreateSubKey(OnKey[..split]);
        parent.CreateSubKey(OnKey[(split + 1)..], true, RegistryOptions.Volatile).Dispose();
    }
    public DisplayTarget[] LoadDark() => File.Exists(DarkPath) ? JsonSerializer.Deserialize<DisplayTarget[]>(File.ReadAllText(DarkPath)) ?? [] : [];
    public void SaveDark(IReadOnlyCollection<DisplayTarget> targets)
    {
        if (targets.Count == 0) { if (File.Exists(DarkPath)) File.Delete(DarkPath); return; }
        Directory.CreateDirectory(Folder);
        File.WriteAllText(DarkPath, JsonSerializer.Serialize(targets));
    }
    public void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = new FileInfo(LogPath);
            if (file.Exists && file.Length > LogLimit)
            {
                var lines = File.ReadAllLines(LogPath);
                File.WriteAllLines(LogPath, lines.Skip(lines.Length / 2));
            }
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* Logging never stops the monitors from waking. */ }
    }
}

// Turns the chosen monitors' panels off over DDC/CI after a stretch without input and back on at the
// next input. Windows is not told: the displays stay attached and keep rendering, so applications,
// window positions and HDR state do not change. Only input counts; video playback or a game's
// keep-the-screen-on request does not keep the panels on (the user's choice, 2026-10-23).
// Page: design canvas https://claude.ai/artifact/7dtoP5r9f84XYigQQUiJQE.
internal sealed class DisplayIdleController : IDisposable
{
    private const int OnTick = 250, DarkTick = 100, SettingsCheck = 5000, VerifyDelay = 2000, RecentLimit = 3;
    // While dark the panels are read again this often; a panel that keeps coming back on is read
    // less and less often, up to WatchLimit.
    private const int WatchInterval = 10_000, WatchLimit = 600_000;
    // After 지금 끄기 the hand is still on the mouse; input this soon after does not wake the panels.
    private const int ManualGrace = 1500;
    private readonly IDisplayPowerControl displays;
    private readonly IIdleInput input;
    private readonly DisplayIdleFiles files;
    private readonly Action<int> sleep;
    private readonly ManualResetEvent stop = new(false);
    private readonly AutoResetEvent signal = new(false);
    private readonly ConcurrentQueue<Action<long>> commands = new();
    private readonly ConcurrentDictionary<string, bool> responding = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DisplayIdleEvent> recent = [];
    private readonly WindowProc windowProc;
    private Thread? thread;
    private DisplayIdleSettings settings = new();
    private DateTime settingsStamp = DateTime.MinValue;
    private long? settingsCheckedAt;
    private long darkAt, checkAt, lastInput;
    private int watchInterval = WatchInterval;
    private bool verified;
    private DateTime? darkSince;
    private string? error;
    private List<DisplayTarget> dark = [];
    private DisplayIdleSnapshot snapshot = new(new(), [], []);
    public bool Dark { get; private set; }
    public DisplayIdleSnapshot Snapshot => Volatile.Read(ref snapshot);
    // Last input on the Environment.TickCount64 clock, for the page's countdown.
    public long LastInput => Interlocked.Read(ref lastInput);
    // Raised on the controller thread.
    public event Action<DisplayIdleSnapshot>? Changed;

    internal DisplayIdleController(IDisplayPowerControl? displays = null, IIdleInput? input = null, DisplayIdleFiles? files = null, Action<int>? sleep = null)
    {
        this.displays = displays ?? new DisplayDdc();
        this.input = input ?? new IdleInput();
        this.files = files ?? new DisplayIdleFiles();
        this.sleep = sleep ?? Thread.Sleep;
        windowProc = OnWindowMessage;
    }

    public void Start()
    {
        if (thread is not null) return;
        thread = new Thread(Run) { IsBackground = true, Name = "Display idle" };
        thread.Start();
    }
    public void Dispose()
    {
        stop.Set();
        if (thread is { IsAlive: true } && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(10));
    }

    // Page commands run on the controller thread at the start of the next tick.
    public void Refresh() => Post(_ => { Probe(); Publish(); });
    public void SetEnabled(bool enabled) => Post(_ => Save(settings with { Enabled = enabled }));
    public void SetIdleSeconds(int seconds) => Post(_ => Save(settings with { IdleSeconds = Math.Max(seconds, 10) }));
    public void SetMonitor(string id, bool selected) => Post(_ => Save(settings with { Monitors = Select(id, selected) }));
    public void DarkenNow() => Post(now => { if (!Dark && settings.Enabled) Darken(now, manual: true); });
    private void Post(Action<long> command) { commands.Enqueue(command); signal.Set(); }

    // A hidden top-level window, so Windows shutdown or sign-out reaches this thread and the panels
    // are lit again first; otherwise they could stay dark at the sign-in screen.
    private void Run()
    {
        nint window = CreateMessageWindow();
        try
        {
            Recover();
            nint[] handles = [stop.SafeWaitHandle.DangerousGetHandle(), signal.SafeWaitHandle.DangerousGetHandle()];
            while (true)
            {
                try { Tick(Environment.TickCount64); }
                catch (Exception e) { files.Log("오류: " + e.Message); }
                uint wait = MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, (uint)(Dark ? DarkTick : OnTick), 0x04FF, 0x4);
                if (wait == 0) break;
                while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
            Wake("앱 종료", quick: true);
        }
        finally { if (window != 0) DestroyWindow(window); }
    }

    // Monitors left dark by a previous run that ended without waking them (crash, forced update).
    internal void Recover()
    {
        try { dark = [.. files.LoadDark()]; }
        catch (Exception e) { files.Log("이전 상태 읽기 실패: " + e.Message); dark = []; }
        if (dark.Count == 0) return;
        Dark = true;
        Wake("앱 시작");
    }

    internal void Tick(long now)
    {
        // Settings first, so a command on the first tick does not save over them with the defaults.
        ReloadSettings(now);
        while (commands.TryDequeue(out var command))
        {
            try { command(now); }
            catch (Exception e) { error = e.Message; files.Log("명령 실패: " + e.Message); Publish(); }
        }
        long last = input.LastInput(now);
        Interlocked.Exchange(ref lastInput, last);
        if (Dark)
        {
            if (last > darkAt) Wake("입력");
            else if (!settings.Enabled) Wake("기능 꺼짐");
            else if (dark.Count > 0 && now >= checkAt) Watch(now);
            return;
        }
        if (settings.Enabled && now - last >= settings.IdleMilliseconds) Darken(now, manual: false);
    }

    private void ReloadSettings(long now)
    {
        if (settingsCheckedAt is long checkedAt && now - checkedAt < SettingsCheck) return;
        settingsCheckedAt = now;
        try
        {
            var stamp = files.SettingsStamp;
            if (stamp == settingsStamp) return;
            settingsStamp = stamp;
            settings = files.LoadSettings();
            LogSettings();
            Publish();
        }
        catch (Exception e) { files.Log("설정 읽기 실패, 이전 설정 유지: " + e.Message); }
    }
    private void Save(DisplayIdleSettings value)
    {
        try
        {
            files.SaveSettings(value);
            settings = value;
            settingsStamp = files.SettingsStamp;
            error = null;
            LogSettings();
        }
        catch (Exception e) { error = "설정을 저장하지 못했습니다. " + e.Message; }
        Publish();
    }
    private void LogSettings() =>
        files.Log($"설정: {(settings.Enabled ? "켜짐" : "꺼짐")}, {settings.IdleMilliseconds / 1000}초, 대상 {string.Join(", ", settings.Selectors)}");

    // Editing one switch turns the selection into device paths, so same-model monitors can differ.
    // Entries for monitors that are not attached now stay as they were.
    private string[] Select(string id, bool selected)
    {
        var attached = displays.List();
        bool Matches(string selector, DisplayInfo m) =>
            string.Equals(selector, m.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(selector, m.Id, StringComparison.OrdinalIgnoreCase);
        var kept = settings.Selectors.Where(s => !attached.Any(m => Matches(s, m)));
        var chosen = attached.Where(m => !m.Internal && (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase) ? selected : settings.Selects(m))).Select(m => m.Id);
        return kept.Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void Probe() => Parallel.ForEach(SafeList().Where(m => !m.Internal), m => responding[m.Id] = ReadPower(m.Id) is not null);

    private void Darken(long now, bool manual)
    {
        // Set before the commands go out, so input that arrives while they run still wakes the panels.
        Dark = true; darkAt = manual ? now + ManualGrace : now; darkSince = DateTime.Now;
        checkAt = darkAt + VerifyDelay; verified = false; watchInterval = WatchInterval;
        var clock = Stopwatch.StartNew();
        var turnedOff = new ConcurrentBag<DisplayTarget>();
        var skipped = new ConcurrentBag<string>();
        Parallel.ForEach(displays.List().Where(settings.Selects), target =>
        {
            // Only panels that are on now: one the user switched off by hand must not come back on later.
            uint? power = ReadPower(target.Id);
            responding[target.Id] = power is not null;
            if (power is null) skipped.Add($"{target.Label} 응답 없음");
            else if (power != 1) skipped.Add($"{target.Label} 이미 꺼짐");
            else if (displays.Set(target.Id, false)) turnedOff.Add(new(target.Id, target.Label));
            else skipped.Add($"{target.Label} 끄기 실패");
        });
        dark = [.. turnedOff.OrderBy(t => t.Name)];
        try { files.SaveDark(dark); } catch (Exception e) { skipped.Add("상태 저장 실패: " + e.Message); }
        var notes = skipped.OrderBy(x => x).ToList();
        string cause = manual ? "지금 끄기" : $"입력 없음 {Duration(settings.IdleMilliseconds / 1000)}";
        files.Log($"{cause} → {(dark.Count + notes.Count == 0 ? "대상 모니터 없음" : string.Join(", ", dark.Select(d => d.Name + " 끔").Concat(notes)))} ({clock.ElapsedMilliseconds} ms)");
        if (dark.Count > 0) notes.Insert(0, $"{dark.Count}대 끔");
        if (notes.Count > 0) AddEvent($"{cause} → {string.Join(" · ", notes)}");
        Publish();
    }

    // The first check, two seconds after the off command, catches a panel that ignored it (some do
    // while their menu is open). The later ones catch a panel that turned itself back on: both
    // MO32U24s did about an hour after D6=5 (2026-10-25..27, the HDMI one re-plugging itself in
    // Windows) and stayed lit until the next input while the page still said 꺼짐.
    private void Watch(long now)
    {
        var lit = new ConcurrentBag<(DisplayTarget Target, bool Off)>();
        Parallel.ForEach(dark, target => { if (ReadPower(target.Id) == 1) lit.Add((target, displays.Set(target.Id, false))); });
        var found = lit.OrderBy(l => l.Target.Name).ToList();
        foreach (var (target, off) in found) files.Log($"{target.Name} {(verified ? "다시 켜져" : "꺼지지 않아")} 다시 끔: {(off ? "성공" : "실패")}");
        if (verified && found.Count > 0)
        {
            var parts = new List<string>();
            if (found.Any(l => l.Off)) parts.Add(string.Join(", ", found.Where(l => l.Off).Select(l => l.Target.Name)) + " 끔");
            parts.AddRange(found.Where(l => !l.Off).Select(l => l.Target.Name + " 끄기 실패"));
            AddEvent($"다시 켜짐 → {string.Join(" · ", parts)}");
            Publish();
        }
        verified = true;
        // Something that keeps switching a panel back on (a person at its power button) is not fought every 10 s.
        watchInterval = found.Count == 0 ? WatchInterval : Math.Min(watchInterval * 2, WatchLimit);
        checkAt = now + watchInterval;
    }

    private void Wake(string reason, bool quick = false)
    {
        if (!Dark) return;
        Dark = false; darkSince = null;
        var targets = dark;
        dark = [];
        if (targets.Count == 0) { TryClearDark(); Publish(); return; }
        var clock = Stopwatch.StartNew();
        var woken = new ConcurrentBag<(DisplayTarget Target, long Ms, int Attempts)>();
        var failed = new ConcurrentBag<string>();
        Parallel.ForEach(targets, target =>
        {
            bool sent = displays.Set(target.Id, true);
            if (quick) { if (sent) woken.Add((target, 0, 1)); else failed.Add(target.Name); return; }
            // The panel takes about two seconds, and DDC/CI answers only now and then while it wakes.
            for (int attempt = 1; ; attempt++)
            {
                if (WaitForOn(target.Id)) { woken.Add((target, clock.ElapsedMilliseconds, attempt)); return; }
                if (attempt == 3) { failed.Add(target.Name); return; }
                displays.Set(target.Id, true);
            }
        });
        TryClearDark();
        var on = woken.OrderBy(w => w.Target.Name).ToList();
        var fails = failed.OrderBy(x => x).ToList();
        files.Log($"{reason} → {string.Join(", ", on.Select(w => quick ? $"{w.Target.Name} 켬" : $"{w.Target.Name} 켬({w.Ms} ms{(w.Attempts > 1 ? $", {w.Attempts}회" : "")})").Concat(fails.Select(f => f + " 켜짐 확인 실패")))}");
        if (!quick)
        {
            var parts = new List<string>();
            if (on.Count > 0) parts.Add(string.Join(", ", on.Select(w => w.Target.Name)) + " 켬");
            parts.AddRange(fails.Select(f => f + " 켜짐 확인 실패"));
            AddEvent($"{reason} → {string.Join(" · ", parts)}", on.Count > 0 ? $"{on.Max(w => w.Ms) / 1000.0:0.0}초" : null);
        }
        Publish();
    }
    private void TryClearDark()
    {
        try { files.SaveDark([]); } catch (Exception e) { files.Log("상태 지우기 실패: " + e.Message); }
    }
    private bool WaitForOn(string id)
    {
        for (int i = 0; i < 12; i++)
        {
            if (displays.Read(id) == 1) return true;
            sleep(250);
        }
        return false;
    }
    private uint? ReadPower(string id)
    {
        for (int attempt = 0; ; attempt++)
        {
            var value = displays.Read(id);
            if (value is not null || attempt == 2) return value;
            sleep(50);
        }
    }
    private void AddEvent(string text, string? note = null)
    {
        recent.Insert(0, new(DateTime.Now, text, note));
        if (recent.Count > RecentLimit) recent.RemoveRange(RecentLimit, recent.Count - RecentLimit);
    }
    private DisplayInfo[] SafeList()
    {
        try { return displays.List(); }
        catch (Exception e) { files.Log("모니터 목록 읽기 실패: " + e.Message); return []; }
    }
    private void Publish()
    {
        var attached = SafeList();
        var primary = attached.FirstOrDefault(m => m.Primary);
        var darkIds = dark.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Primary first, then the same model as the primary, others by name, the laptop panel last.
        var monitors = attached.OrderBy(m => m.Internal).ThenByDescending(m => m.Primary)
            .ThenByDescending(m => primary is not null && string.Equals(m.Name, primary.Name, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Label, StringComparer.CurrentCulture)
            .Select(m => new DisplayIdleMonitor(m.Id, m.Label, Describe(m, primary), !m.Internal, settings.Selects(m),
                m.Internal ? null : responding.TryGetValue(m.Id, out bool answers) ? answers : null, darkIds.Contains(m.Id)))
            .ToArray();
        var value = new DisplayIdleSnapshot(settings, monitors, [.. recent], Dark, darkSince, dark.Count, error);
        Volatile.Write(ref snapshot, value);
        Changed?.Invoke(value);
    }

    internal static string Describe(DisplayInfo monitor, DisplayInfo? primary)
    {
        if (monitor.Internal) return "노트북 화면은 DDC/CI를 지원하지 않아 끌 수 없습니다.";
        string place = monitor.Primary ? "주 모니터" : primary is null ? "" : Direction(monitor, primary);
        string connection = monitor.Technology switch { 0 => "VGA", 4 => "DVI", 5 => "HDMI", 10 => "DisplayPort", 15 => "무선", 18 => "USB-C", _ => "" };
        return string.Join(" · ", new[] { place, connection }.Where(s => s.Length > 0));
    }
    private static string Direction(DisplayInfo m, DisplayInfo p)
    {
        double dx = m.X + m.Width / 2.0 - (p.X + p.Width / 2.0), dy = m.Y + m.Height / 2.0 - (p.Y + p.Height / 2.0);
        return Math.Abs(dy) > Math.Abs(dx) ? dy < 0 ? "주 모니터 위" : "주 모니터 아래" : dx < 0 ? "주 모니터 왼쪽" : "주 모니터 오른쪽";
    }
    internal static string Duration(long seconds) =>
        seconds < 60 ? $"{seconds}초" : seconds % 60 == 0 ? $"{seconds / 60}분" : $"{seconds / 60}분 {seconds % 60}초";
    // 이/가 after a monitor name: the final consonant of Hangul, or of how a digit or letter is read.
    internal static string Subject(string name)
    {
        char last = name.TrimEnd().LastOrDefault();
        if (last is >= '가' and <= '힣') return (last - '가') % 28 != 0 ? "이" : "가";
        if (char.IsDigit(last)) return "013678".Contains(last) ? "이" : "가";
        return "LMNRlmnr".Contains(last) ? "이" : "가";
    }

    private nint OnWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x11) return 1; // WM_QUERYENDSESSION: never block shutdown.
        if (message == 0x16 && wParam != 0) Wake("Windows 종료", quick: true); // WM_ENDSESSION
        return DefWindowProc(window, message, wParam, lParam);
    }
    private nint CreateMessageWindow()
    {
        var module = GetModuleHandle(null);
        var windowClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = windowProc, Instance = module, ClassName = "PersonalTools.DisplayIdle" };
        RegisterClassEx(ref windowClass);
        // Top-level and never shown: message-only windows do not get the end-session broadcast.
        return CreateWindowEx(0, windowClass.ClassName, "Personal Tools display idle", 0, 0, 0, 0, 0, 0, 0, module, 0);
    }

    private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public WindowProc Proc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[] handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
