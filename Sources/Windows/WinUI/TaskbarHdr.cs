using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PersonalTools;

// An HDR button at the right end of every monitor's taskbar (design canvas
// https://claude.ai/artifact/M8LZqXcTbkkArBSbxrbSPG, variant A). Windows 11 has no API for taskbar buttons, so each is
// a layered child window inside Explorer's Shell_TrayWnd or Shell_SecondaryTrayWnd: it moves and hides with its
// taskbar, under full-screen games too. A child window shares Explorer's input queue, so the buttons live on a thread
// of their own that never waits: HDR changes and UI Automation run on the thread pool and post their results back.
internal sealed class TaskbarHdrButtons : IDisposable
{
    private const string ButtonClass = "PersonalTools.TaskbarHdr", HostClass = "PersonalTools.TaskbarHdrHost", TipClass = "PersonalTools.TaskbarHdrTip";
    private const uint SnapshotMessage = 0x8001, DoneMessage = 0x8002, MeasuredMessage = 0x8003, PreviewMessage = 0x8004;
    private const nuint RelayoutTimer = 1, RescanTimer = 2, PeriodicTimer = 3, AnimateTimer = 4, TipTimer = 5, PreviewMenuTimer = 6;
    // A secondary taskbar's clock is about 70 DIP wide and ends 8 DIP from the edge; used until UI Automation answers.
    private const int ClockFallbackDip = 79, GapDip = 6;
    private readonly ITaskbarHdrSource source;
    private readonly Action<bool> open;
    private readonly ManualResetEventSlim ready = new();
    private readonly ConcurrentQueue<(string Id, string? Failure)> finished = new();
    private readonly ConcurrentQueue<(nint Taskbar, Rect Bounds, int? Left)> measured = new();
    private readonly Dictionary<nint, Button> buttons = [], windows = [];
    private readonly List<nint> hooks = [];
    private readonly WindowProc proc;
    private readonly WinEventProc winEvent;
    private Thread? thread;
    private volatile nint host;
    private nint tip, tray, instance, tipFont, tipStrong;
    private uint taskbarCreated, explorer;
    private volatile HdrSnapshot latest;
    private HdrSnapshot snapshot;
    private List<(nint Window, bool Primary, string? Monitor)> taskbars = [];
    private TaskbarHdrTheme theme;
    private int themeVersion, tipDpi;
    private Button? tipOwner;
    private (string Text, bool Strong, bool Muted)[] tipLines = [];
    private volatile nint[] published = [];

    private sealed class Button
    {
        public nint Taskbar, Window;
        public bool Primary, Measuring, Hover, Pressed, Shown;
        public string MonitorId = "";
        public Rect Bounds, Place;
        public int Dpi, Size, ThemeVersion = -1;
        public int? TrayLeft;   // UI Automation's reading for Bounds
        public bool? Busy;      // the state being switched to
        public string? Failure;
        public TaskbarHdrLook Look;
    }

    public TaskbarHdrButtons(ITaskbarHdrSource source, Action<bool> open)
    {
        this.source = source;
        this.open = open;
        latest = snapshot = source.Snapshot;
        proc = WindowMessage;
        winEvent = WindowEvent;
    }
    public void Start()
    {
        if (thread is not null) return;
        thread = new Thread(Run) { IsBackground = true, Name = "Taskbar HDR" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        source.Changed += Changed;
        Changed(source.Snapshot);
    }
    public void Dispose()
    {
        source.Changed -= Changed;
        if (host != 0) PostMessage(host, 0x10, 0, 0); // WM_CLOSE
        thread?.Join(TimeSpan.FromSeconds(2));
    }
    // For the test harness: the button windows, and a plain (0), hover (1) or menu (2) look without the real mouse.
    internal nint[] Windows => published;
    internal void Preview(int index, int what) { if (host != 0) PostMessage(host, PreviewMessage, index, what); }
    private void Changed(HdrSnapshot value)
    {
        latest = value;
        if (host != 0) PostMessage(host, SnapshotMessage, 0, 0);
    }

    private void Run()
    {
        try
        {
            instance = GetModuleHandle(null);
            foreach (var (name, style) in new[] { (ButtonClass, 0u), (HostClass, 0u), (TipClass, 0x20000u) }) // CS_DROPSHADOW
            {
                var type = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Style = style, Proc = Marshal.GetFunctionPointerForDelegate(proc),
                    Instance = instance, Cursor = LoadCursor(0, 32512), ClassName = name };
                RegisterClassEx(ref type);
            }
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            theme = TaskbarHdrTheme.Read();
            // A hidden top-level window: it hears TaskbarCreated and other broadcasts and owns the menus.
            host = CreateWindowEx(0x80, HostClass, "Personal Tools 작업표시줄 HDR", 0x80000000, 0, 0, 0, 0, 0, 0, instance, 0);
            // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST
            tip = CreateWindowEx(0x08000000 | 0x80 | 0x20 | 0x8, TipClass, "", 0x80000000, 0, 0, 0, 0, 0, 0, instance, 0);
            int corner = 3; // DWMWCP_ROUNDSMALL, the 4 px of WinUI tooltips
            DwmSetWindowAttribute(tip, 33, ref corner, sizeof(int));
            ready.Set();
            if (host == 0) return;
            Rescan();
            SetTimer(host, PeriodicTimer, 60_000, 0);
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception e) { Log(e); }
        finally { ready.Set(); }
    }

    // An exception must never cross back into user32, so every message is handled inside a catch.
    private nint WindowMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (hwnd == host && hwnd != 0) { if (HostMessage(message, wParam, lParam)) return 0; }
            else if (hwnd == tip && hwnd != 0)
            {
                if (message == 0xF) { PaintTip(); return 0; }   // WM_PAINT
                if (message == 0x2E0) return 0;                  // WM_DPICHANGED: UpdateTip sizes it for the taskbar's DPI
            }
            else if (windows.TryGetValue(hwnd, out var button) && ButtonMessage(button, message, wParam, lParam) is { } result) return result;
        }
        catch (Exception e) { Log(e); }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    private bool HostMessage(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case SnapshotMessage: snapshot = latest; Apply(); return true;
            case DoneMessage: Finish(); return true;
            case MeasuredMessage: Measured(); return true;
            case PreviewMessage: ShowPreview((int)wParam, (int)lParam); return true;
            case 0x113: Tick((nuint)wParam); return true;                  // WM_TIMER
            case 0x7E: Schedule(RescanTimer, 500); return true;             // WM_DISPLAYCHANGE
            case 0x1A:                                                       // WM_SETTINGCHANGE
                if (lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                {
                    theme = TaskbarHdrTheme.Read();
                    themeVersion++;
                    foreach (var b in buttons.Values) Render(b);
                }
                Schedule(RescanTimer, 500);
                return true;
            case 0x10: DestroyWindow(host); return true;                    // WM_CLOSE
            case 0x2: Shutdown(); return true;                              // WM_DESTROY
        }
        if (message != taskbarCreated || message == 0) return false;
        Schedule(RescanTimer, 200);
        return true;
    }
    private void Tick(nuint id)
    {
        if (id is RelayoutTimer or RescanTimer or PreviewMenuTimer) KillTimer(host, id);
        if (id == RelayoutTimer) foreach (var b in buttons.Values.ToArray()) { Layout(b); Render(b); }
        else if (id == RescanTimer) Rescan();
        else if (id == PeriodicTimer)
        {
            // The clock can change width with the date or time format, and nothing announces it.
            Rescan();
            foreach (var b in buttons.Values) if (!b.Primary || tray == 0) Measure(b);
        }
        else if (id == AnimateTimer)
        {
            var busy = buttons.Values.Where(b => b.Busy is not null).ToArray();
            foreach (var b in busy) Render(b);
            if (busy.Length == 0) KillTimer(host, AnimateTimer);
        }
        else if (id == PreviewMenuTimer) EndMenu();
    }
    private void Schedule(nuint id, uint delay) => SetTimer(host, id, delay, 0);

    // Finds the taskbars and their monitors again; for changes in windows or displays, not in HDR state.
    private void Rescan()
    {
        var found = new List<(nint, bool)>();
        nint primary = FindWindowEx(0, 0, "Shell_TrayWnd", null);
        if (primary != 0) found.Add((primary, true));
        for (nint window = 0; (window = FindWindowEx(0, window, "Shell_SecondaryTrayWnd", null)) != 0;) found.Add((window, false));
        tray = primary == 0 ? 0 : FindWindowEx(primary, 0, "TrayNotifyWnd", null);
        Dictionary<string, string> names;
        try { names = HdrDisplay.GdiNames(); }
        catch { names = new(); }
        taskbars = found.Select(t => (t.Item1, t.Item2, MonitorOf(t.Item1, names))).ToList();
        if (primary != 0 && GetWindowThreadProcessId(primary, out uint pid) != 0 && pid != explorer) Hook(pid);
        Apply();
    }
    private static string? MonitorOf(nint taskbar, Dictionary<string, string> names)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(taskbar, 2), ref info) && names.TryGetValue(info.Device, out var id) ? id : null;
    }
    // Out of context, so Explorer calls back through this thread's message loop and loads nothing of ours.
    private void Hook(uint pid)
    {
        foreach (var hook in hooks) UnhookWinEvent(hook);
        hooks.Clear();
        explorer = pid;
        hooks.Add(SetWinEventHook(0x8000, 0x8001, 0, winEvent, pid, 0, 0)); // EVENT_OBJECT_CREATE, EVENT_OBJECT_DESTROY
        hooks.Add(SetWinEventHook(0x800B, 0x800B, 0, winEvent, pid, 0, 0)); // EVENT_OBJECT_LOCATIONCHANGE
    }
    private void WindowEvent(nint hook, uint kind, nint hwnd, int item, int child, uint thread, uint time)
    {
        try
        {
            if (item != 0 || child != 0 || hwnd == 0) return; // OBJID_WINDOW: the window itself, not a caret or a part
            // The tray grows when an icon appears (the microphone one, say); follow it at once instead of overlapping it.
            if (kind == 0x800B) { if (hwnd == tray || buttons.ContainsKey(hwnd)) Schedule(RelayoutTimer, 15); }
            else if (kind == 0x8001 ? taskbars.Any(t => t.Window == hwnd) : IsTaskbar(hwnd)) Schedule(RescanTimer, 300);
        }
        catch (Exception e) { Log(e); }
    }
    private static bool IsTaskbar(nint hwnd)
    {
        var name = new char[32];
        int length = GetClassName(hwnd, name, name.Length);
        return new string(name, 0, Math.Max(0, length)) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    // Brings the buttons in line with the taskbars and the latest snapshot. Runs every second with the snapshot, so it
    // is also the fallback for any taskbar move the events missed.
    private void Apply()
    {
        var s = snapshot;
        var hidden = (s.TaskbarHidden ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = taskbars.Where(t => s.TaskbarButtons && t.Monitor is { } id && !hidden.Contains(id) && IsWindow(t.Window)
            && s.Monitors.Any(m => m.Id == id && m.Supported)).ToDictionary(t => t.Window);
        foreach (var b in buttons.Values.Where(b => !wanted.ContainsKey(b.Taskbar) || !IsWindow(b.Window)).ToArray()) Remove(b);
        foreach (var (taskbar, primary, id) in wanted.Values)
        {
            if (!buttons.TryGetValue(taskbar, out var b) && (b = Create(taskbar, primary)) is null) continue;
            b.MonitorId = id!;
            b.Primary = primary;
            Layout(b);
            Render(b);
        }
        published = buttons.Values.Select(b => b.Window).ToArray();
        if (tipOwner is not null) UpdateTip();
    }
    private Button? Create(nint taskbar, bool primary)
    {
        // WS_EX_LAYERED | WS_EX_NOPARENTNOTIFY: per-pixel alpha, and nothing is sent to Explorer's window for it.
        nint window = CreateWindowEx(0x80000 | 0x4, ButtonClass, "HDR", 0x40000000 | 0x04000000, 0, 0, 1, 1, taskbar, 0, instance, 0); // WS_CHILD | WS_CLIPSIBLINGS
        if (window == 0) return null;
        var b = new Button { Taskbar = taskbar, Window = window, Primary = primary };
        buttons[taskbar] = b;
        windows[window] = b;
        return b;
    }
    private void Remove(Button b)
    {
        if (tipOwner == b) HideTip();
        buttons.Remove(b.Taskbar);
        windows.Remove(b.Window);
        if (IsWindow(b.Window)) DestroyWindow(b.Window);
    }

    private void Layout(Button b)
    {
        if (!GetWindowRect(b.Taskbar, out var bar)) return;
        int dpi = (int)GetDpiForWindow(b.Taskbar);
        if (dpi <= 0) dpi = 96;
        if (bar != b.Bounds || dpi != b.Dpi)
        {
            b.Bounds = bar;
            b.Dpi = dpi;
            b.TrayLeft = null;
            if (!b.Primary) Measure(b);
        }
        double scale = dpi / 96.0;
        int anchor;
        if (b.Primary && tray != 0 && GetWindowRect(tray, out var area) && area.Left > bar.Left) anchor = area.Left;
        else
        {
            if (b.Primary && b.TrayLeft is null) Measure(b);
            anchor = b.TrayLeft ?? bar.Right - (int)Math.Round(ClockFallbackDip * scale);
        }
        var place = Place(bar, anchor, scale);
        if (place != b.Place)
        {
            b.Place = place;
            if (place.Right > place.Left) SetWindowPos(b.Window, 0, place.Left, place.Top, place.Right - place.Left, place.Bottom - place.Top, 0x4 | 0x10); // SWP_NOZORDER | SWP_NOACTIVATE
        }
        // Explorer may raise its XAML bridge window over ours; stay on top among the taskbar's children.
        if (GetWindow(b.Window, 3) != 0) SetWindowPos(b.Window, 0, 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // GW_HWNDPREV; HWND_TOP, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
    }
    // The button in the taskbar's client coordinates: a 40 DIP square 6 DIP left of the tray or clock, centred
    // vertically. Empty for a vertical taskbar, or when the tray reaches past the middle.
    internal static Rect Place(Rect bar, int trayLeft, double scale)
    {
        int size = TaskbarHdrPaint.Size(scale), width = bar.Right - bar.Left, height = bar.Bottom - bar.Top;
        int x = trayLeft - bar.Left - (int)Math.Round(GapDip * scale) - size, y = (height - size) / 2;
        return height > width || height < size || x < width / 2 ? default : new(x, y, x + size, y + size);
    }

    private void Measure(Button b)
    {
        if (b.Measuring) return;
        b.Measuring = true;
        var (taskbar, bounds) = (b.Taskbar, b.Bounds);
        _ = Task.Run(() =>
        {
            measured.Enqueue((taskbar, bounds, TaskbarUia.TrayLeft(taskbar)));
            PostMessage(host, MeasuredMessage, 0, 0);
        });
    }
    private void Measured()
    {
        while (measured.TryDequeue(out var result))
        {
            if (!buttons.TryGetValue(result.Taskbar, out var b)) continue;
            b.Measuring = false;
            if (result.Bounds != b.Bounds) { Measure(b); continue; }
            if (result.Left is { } left && left > b.Bounds.Left && left <= b.Bounds.Right) b.TrayLeft = left;
            Layout(b);
            Render(b);
        }
    }

    private (TaskbarHdrKind Kind, bool Lit, HdrMonitor? Monitor, HdrActive? Session) StateOf(Button b)
    {
        var monitor = snapshot.Monitors.FirstOrDefault(m => m.Id == b.MonitorId);
        var session = snapshot.Active?.FirstOrDefault(a => a.MonitorId == b.MonitorId);
        if (monitor is null) return (TaskbarHdrKind.Off, false, null, session);
        if (monitor.Error is not null) return (TaskbarHdrKind.Restricted, false, monitor, session);
        if (b.Busy is { } target) return (TaskbarHdrKind.Busy, target, monitor, session);
        // Turned on by automation for a running game and not overridden: the dot says it goes back off afterwards.
        if (monitor.Enabled && session is { Manual: false, Original: false }) return (TaskbarHdrKind.Auto, true, monitor, session);
        return (monitor.Enabled ? TaskbarHdrKind.On : TaskbarHdrKind.Off, monitor.Enabled, monitor, session);
    }
    private void Render(Button b)
    {
        if (!windows.ContainsKey(b.Window)) return;
        int size = b.Place.Right - b.Place.Left;
        if (size <= 0)
        {
            if (b.Shown) ShowWindow(b.Window, 0);
            b.Shown = false;
            return;
        }
        var (kind, lit, _, _) = StateOf(b);
        var look = new TaskbarHdrLook(kind, lit, b.Hover, b.Pressed, kind == TaskbarHdrKind.Busy ? Environment.TickCount64 % 1000 / 1000.0 : 0);
        if (b.Shown && look == b.Look && size == b.Size && b.ThemeVersion == themeVersion) return;
        var pixels = TaskbarHdrPaint.Render(look, theme, b.Dpi / 96.0);
        if (pixels.Length != size * size || !Push(b.Window, pixels, size)) return;
        b.Look = look;
        b.Size = size;
        b.ThemeVersion = themeVersion;
        if (!b.Shown) ShowWindow(b.Window, 8); // SW_SHOWNA
        b.Shown = true;
    }
    private static bool Push(nint window, int[] pixels, int size)
    {
        nint screen = GetDC(0), dc = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32 };
        nint bitmap = CreateDIBSection(dc, ref info, 0, out nint bits, 0, 0);
        try
        {
            if (bitmap == 0) return false;
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            nint old = SelectObject(dc, bitmap);
            var extent = new Extent { Cx = size, Cy = size };
            var origin = new Point();
            var blend = new Blend { Alpha = 255, Format = 1 }; // AC_SRC_OVER with AC_SRC_ALPHA
            bool pushed = UpdateLayeredWindow(window, screen, 0, ref extent, dc, ref origin, 0, ref blend, 2); // ULW_ALPHA
            SelectObject(dc, old);
            return pushed;
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }

    private nint? ButtonMessage(Button b, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x21: return 3; // WM_MOUSEACTIVATE → MA_NOACTIVATE: a click leaves the foreground window (a game, say) alone
            case 0x200:          // WM_MOUSEMOVE
                if (b.Hover) return 0;
                b.Hover = true;
                var track = new TrackMouse { Size = (uint)Marshal.SizeOf<TrackMouse>(), Flags = 2, Window = b.Window }; // TME_LEAVE
                TrackMouseEvent(ref track);
                SetTimer(b.Window, TipTimer, GetDoubleClickTime(), 0); // the system's initial tooltip delay
                Render(b);
                return 0;
            case 0x2A3:          // WM_MOUSELEAVE
                b.Hover = false;
                KillTimer(b.Window, TipTimer);
                HideTip(b);
                Render(b);
                return 0;
            case 0x113 when (nuint)wParam == TipTimer:
                KillTimer(b.Window, TipTimer);
                if (b.Hover && !b.Pressed) { tipOwner = b; UpdateTip(); }
                return 0;
            case 0x201:          // WM_LBUTTONDOWN
                b.Pressed = true;
                SetCapture(b.Window);
                KillTimer(b.Window, TipTimer);
                HideTip(b);
                Render(b);
                return 0;
            case 0x202:          // WM_LBUTTONUP
                if (!b.Pressed) return 0;
                b.Pressed = false;
                ReleaseCapture();
                int x = (short)(lParam & 0xFFFF), y = (short)(lParam >> 16 & 0xFFFF);
                if (x >= 0 && y >= 0 && x < b.Size && y < b.Size) Toggle(b);
                Render(b);
                return 0;
            case 0x215:          // WM_CAPTURECHANGED
                if (b.Pressed) { b.Pressed = false; Render(b); }
                return 0;
            case 0x205:          // WM_RBUTTONUP
                KillTimer(b.Window, TipTimer);
                Menu(b, true);
                return 0;
            case 0x2:            // WM_DESTROY, also when Explorer's taskbar goes away
                windows.Remove(b.Window);
                if (buttons.TryGetValue(b.Taskbar, out var same) && same == b) buttons.Remove(b.Taskbar);
                if (tipOwner == b) HideTip();
                return 0;
        }
        return null;
    }
    private void Toggle(Button b)
    {
        var (kind, _, monitor, _) = StateOf(b);
        if (monitor is null || kind is TaskbarHdrKind.Busy or TaskbarHdrKind.Restricted) return;
        bool target = !monitor.Enabled;
        string id = b.MonitorId;
        b.Busy = target;
        b.Failure = null;
        SetTimer(host, AnimateTimer, 33, 0);
        Render(b);
        // The same manual change as the HDR page: during a game it wins over automation until the game ends.
        _ = Task.Run(async () =>
        {
            string? failure = null;
            try { await source.RequestAsync(new("set", id, target)); }
            catch (Exception e) { failure = e.Message; }
            finished.Enqueue((id, failure));
            PostMessage(host, DoneMessage, 0, 0);
        });
    }
    private void Finish()
    {
        snapshot = latest;
        while (finished.TryDequeue(out var done))
            foreach (var b in buttons.Values.Where(b => b.MonitorId == done.Id))
            {
                b.Busy = null;
                b.Failure = done.Failure;
                Render(b);
                if (done.Failure is not null && b.Hover) tipOwner = b;
            }
        if (tipOwner is not null) UpdateTip();
    }

    private void Menu(Button b, bool user)
    {
        HideTip();
        var (kind, _, monitor, _) = StateOf(b);
        if (monitor is null) return;
        // Undocumented uxtheme exports that Explorer's own menus rely on: SetPreferredAppMode (#135) and
        // FlushMenuThemes (#136). They make this process's Win32 menus follow the taskbar theme.
        try { SetPreferredAppMode(theme.Light ? 3 : 2); FlushMenuThemes(); }
        catch { }
        nint menu = CreatePopupMenu();
        var bitmaps = new List<nint>();
        try
        {
            int icon = (int)Math.Round(16 * b.Dpi / 96.0);
            void Add(uint id, string text, char glyph, bool enabled = true)
            {
                AppendMenu(menu, enabled ? 0u : 1u, id, text); // MF_GRAYED
                if (glyph == '\0') return;
                nint bitmap = TaskbarHdrPaint.Glyph(glyph, icon, theme.Light ? 0x1A1A1Au : 0xFFFFFFu);
                if (bitmap == 0) return;
                bitmaps.Add(bitmap);
                var item = new MenuItem { Size = (uint)Marshal.SizeOf<MenuItem>(), Mask = 0x80, Bitmap = bitmap }; // MIIM_BITMAP
                SetMenuItemInfo(menu, id, false, ref item);
            }
            Add(9, Title(monitor), '\0', false);
            Add(1, monitor.Enabled ? "HDR 끄기" : "HDR 켜기", '', kind is not (TaskbarHdrKind.Busy or TaskbarHdrKind.Restricted));
            Add(2, "Windows HDR 설정", '');
            Add(3, "자동 HDR 열기", '');
            AppendMenu(menu, 0x800, 0, null); // MF_SEPARATOR
            Add(4, "작업표시줄 버튼 설정", '');
            GetCursorPos(out var point);
            if (user) SetForegroundWindow(host); // or an outside click would not close the menu; the click that opened it allows this
            else
            {
                GetWindowRect(b.Window, out var button);
                point = new() { X = button.Left, Y = button.Top };
                SetTimer(host, PreviewMenuTimer, 1500, 0);
            }
            int chosen = TrackPopupMenuEx(menu, 0x100 | 0x20 | 0x2, point.X, point.Y, host, 0); // TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON
            PostMessage(host, 0, 0, 0);
            if (!windows.ContainsKey(b.Window)) return;
            if (chosen == 1) Toggle(b);
            else if (chosen == 2) _ = Task.Run(() => { try { Process.Start(new ProcessStartInfo("ms-settings:display-hdr") { UseShellExecute = true })?.Dispose(); } catch { } });
            else if (chosen is 3 or 4) open(chosen == 4);
        }
        finally
        {
            DestroyMenu(menu);
            foreach (var bitmap in bitmaps) DeleteObject(bitmap);
        }
    }
    private static string Title(HdrMonitor monitor) => monitor.Name + (monitor.Primary ? " · 주 모니터" : monitor.Internal ? " · 노트북 화면" : "");

    private (string Text, bool Strong, bool Muted)[] TipText(Button b)
    {
        var (kind, lit, monitor, session) = StateOf(b);
        if (monitor is null) return [];
        string programs = session is null ? "" : string.Join(", ", session.Programs);
        var lines = new List<(string, bool, bool)>
        {
            (Title(monitor), true, false),
            (kind switch
            {
                TaskbarHdrKind.Busy => lit ? "HDR 켜는 중…" : "HDR 끄는 중…",
                TaskbarHdrKind.Auto => programs.Length > 0 ? $"HDR 켜짐 · 자동 HDR ({programs})" : "HDR 켜짐 · 자동 HDR",
                TaskbarHdrKind.Restricted => "HDR을 켤 수 없습니다",
                TaskbarHdrKind.On => "HDR 켜짐 · 누르면 끕니다",
                _ => "HDR 꺼짐 · 누르면 켭니다",
            }, false, false),
        };
        if (kind == TaskbarHdrKind.Auto) lines.Add(("누르면 게임이 끝날 때까지 직접 고른 설정을 따릅니다", false, true));
        else if (kind == TaskbarHdrKind.Restricted) lines.Add((monitor.Error!, false, true));
        else if (b.Failure is not null && kind != TaskbarHdrKind.Busy) lines.Add(("바꾸지 못했습니다: " + b.Failure, false, true));
        return lines.ToArray();
    }
    private void UpdateTip()
    {
        if (tipOwner is not { } b || !windows.ContainsKey(b.Window) || !b.Shown || (tipLines = TipText(b)).Length == 0) { HideTip(); return; }
        double scale = b.Dpi / 96.0;
        if (tipDpi != b.Dpi)
        {
            if (tipFont != 0) DeleteObject(tipFont);
            if (tipStrong != 0) DeleteObject(tipStrong);
            int em = (int)Math.Round(12 * scale);
            tipFont = CreateFont(-em, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"); // CLEARTYPE_QUALITY
            tipStrong = CreateFont(-em, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            tipDpi = b.Dpi;
        }
        nint dc = GetDC(tip);
        int width = 0;
        foreach (var line in tipLines)
        {
            nint old = SelectObject(dc, line.Strong ? tipStrong : tipFont);
            GetTextExtentPoint32(dc, line.Text, line.Text.Length, out var extent);
            SelectObject(dc, old);
            width = Math.Max(width, extent.Cx);
        }
        ReleaseDC(tip, dc);
        width = Math.Min(width, Px(420, scale)) + 2 * Px(9, scale);
        int height = Px(13, scale) + tipLines.Length * Px(16, scale) + (tipLines.Length - 1) * Px(2, scale);
        GetWindowRect(b.Window, out var button);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(b.Taskbar, 2), ref info);
        int x = Math.Clamp((button.Left + button.Right - width) / 2, info.Monitor.Left, Math.Max(info.Monitor.Left, info.Monitor.Right - width));
        int y = b.Bounds.Top - Px(8, scale) - height;
        if (y < info.Monitor.Top) y = b.Bounds.Bottom + Px(8, scale);
        int border = theme.Light ? 0xE5E5E5 : 0x3D3D3D;
        DwmSetWindowAttribute(tip, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
        SetWindowPos(tip, -1, x, y, width, height, 0x10 | 0x40); // HWND_TOPMOST, SWP_NOACTIVATE | SWP_SHOWWINDOW
        InvalidateRect(tip, 0, true);
    }
    private void PaintTip()
    {
        nint dc = BeginPaint(tip, out var paint);
        try
        {
            GetClientRect(tip, out var area);
            var (back, text, muted) = theme.Light ? (0xF9F9F9u, 0x1A1A1Au, 0x5D5D5Du) : (0x2B2B2Bu, 0xFFFFFFu, 0xC8C8C8u);
            nint brush = CreateSolidBrush(back);
            FillRect(dc, ref area, brush);
            DeleteObject(brush);
            SetBkMode(dc, 1);
            double scale = tipDpi / 96.0;
            int y = Px(6, scale), line = Px(16, scale), pad = Px(9, scale);
            foreach (var (content, strong, quiet) in tipLines)
            {
                SelectObject(dc, strong ? tipStrong : tipFont);
                SetTextColor(dc, quiet ? muted : text);
                var row = new Rect(pad, y, area.Right - pad, y + line);
                DrawText(dc, content, content.Length, ref row, 0x20 | 0x4 | 0x8000 | 0x800); // SINGLELINE | VCENTER | END_ELLIPSIS | NOPREFIX
                y += line + Px(2, scale);
            }
        }
        finally { EndPaint(tip, ref paint); }
    }
    private void HideTip(Button? b = null)
    {
        if (b is not null && tipOwner != b) return;
        tipOwner = null;
        if (tip != 0) ShowWindow(tip, 0);
    }
    private static int Px(double dip, double scale) => (int)Math.Round(dip * scale);

    private void ShowPreview(int index, int what)
    {
        var all = buttons.Values.ToArray();
        if (index < 0 || index >= all.Length) return;
        var b = all[index];
        if (what == 2) { Menu(b, false); return; }
        b.Hover = what == 1;
        Render(b);
        if (b.Hover) { tipOwner = b; UpdateTip(); }
        else HideTip(b);
    }

    private void Shutdown()
    {
        foreach (var hook in hooks) UnhookWinEvent(hook);
        hooks.Clear();
        foreach (var b in buttons.Values.ToArray()) Remove(b);
        if (tip != 0) DestroyWindow(tip);
        if (tipFont != 0) DeleteObject(tipFont);
        if (tipStrong != 0) DeleteObject(tipStrong);
        PostQuitMessage(0);
    }
    private static void Log(Exception error)
    {
        try
        {
            Directory.CreateDirectory(HdrFiles.DirectoryPath);
            var path = Path.Combine(HdrFiles.DirectoryPath, "taskbar.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}\n");
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Rect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Extent { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse { public uint Size, Flags; public nint Window; public uint HoverTime; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItem
    {
        public uint Size, Mask, Type, State, Id;
        public nint SubMenu, Checked, Unchecked;
        public nuint Data;
        public nint TypeData;
        public uint Length;
        public nint Bitmap;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintInfo
    {
        public nint Dc; public int Erase; public Rect Area; public int Restore, IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public nint Proc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    private delegate void WinEventProc(nint hook, uint kind, nint hwnd, int item, int child, uint thread, uint time);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string type, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint elapse, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string type, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, char[] name, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint relation);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(nint window, nint screen, nint position, ref Extent size, nint source, ref Point origin, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse track);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll", EntryPoint = "SetMenuItemInfoW")] private static extern bool SetMenuItemInfo(nint menu, uint item, bool byPosition, ref MenuItem info);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [DllImport("user32.dll")] private static extern bool EndMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadCursorW")] private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint window, out PaintInfo paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint window, ref PaintInfo paint);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")] private static extern int DrawText(nint dc, string text, int length, ref Rect rect, uint format);
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike,
        uint charset, uint precision, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")] private static extern bool GetTextExtentPoint32(nint dc, string text, int length, out Extent size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
}
