using System.Runtime.InteropServices;
using System.Text;
using PersonalTools;

// Unit checks always run. --live creates real windows; --live-keys also injects
// Win+Shift+1 / Win+1 through the real low-level hook (briefly uses the keyboard).
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; }

Check(WindowSlotKeys.Classify('1', win: true, shift: false, control: false, alt: false) == (WindowKeyAction.Activate, 0), "Win+1 activates slot 1");
Check(WindowSlotKeys.Classify('R', true, true, false, false) == (WindowKeyAction.Register, 7), "Win+Shift+R registers slot R");
Check(WindowSlotKeys.Classify('Q', true, false, false, false).Slot == 4 && WindowSlotKeys.Classify('W', true, false, false, false).Slot == 5, "letter slots follow the keyboard order");
Check(WindowSlotKeys.Classify('1', true, false, true, false).Action == WindowKeyAction.None, "Ctrl+Win+1 is left to Windows");
Check(WindowSlotKeys.Classify('E', true, false, false, true).Action == WindowKeyAction.None, "Alt+Win+E is left to Windows");
Check(WindowSlotKeys.Classify('1', false, true, false, false).Action == WindowKeyAction.None, "Shift+1 without Win is ignored");
Check(WindowSlotKeys.Classify('5', true, false, false, false).Action == WindowKeyAction.None && WindowSlotKeys.Classify('T', true, false, false, false).Action == WindowKeyAction.None, "only 1-4 and Q/W/E/R are consumed");

var table = new WindowSlotTable();
var a = new WindowSlot(101, 1, 2, "문서 A", "편집기", null);
table.Assign(0, a); table.Assign(4, a); table.Assign(1, a with { Handle = 202, Title = "B" });
Check(table.Count == 3 && table.Get(4) == a, "slots store registrations independently");
Check(table.UpdateTitle(101, "문서 A - 수정됨") && table.Get(0)!.Title == "문서 A - 수정됨" && table.Get(4)!.Title == "문서 A - 수정됨", "title change updates every slot of that window");
Check(table.ClearHandle(101).SequenceEqual([0, 4]) && table.Count == 1 && table.Contains(202), "closing a window clears all of its slots only");
Check(table.Clear(1) && !table.Clear(1) && table.Count == 0, "manual unregister");
var snapshot = table.Snapshot(); table.Assign(2, a);
Check(snapshot[2] is null, "snapshots are copies");

if (args.Contains("--live") || args.Contains("--live-keys"))
{
    nint first = Native.Create("PersonalTools switch test A"), second = Native.Create("PersonalTools switch test B");
    try
    {
        Native.Pump(300);
        var described = WindowShortcuts.Describe(first);
        Check(described is not null && described.Handle == first && described.Title == "PersonalTools switch test A" && described.AppName.Length > 0, "describes a real top-level window");
        Check(WindowShortcuts.Describe(Native.FindWindow("Shell_TrayWnd", null)) is null, "taskbar cannot be registered");
        Check(WindowShortcuts.IsAlive(described!), "live window recognised");
        Check(WindowShortcuts.Focus(second) && Native.Foreground() == second, "brings window B to the front from a background process");
        Native.Pump(200);
        Check(WindowShortcuts.Focus(first) && Native.Foreground() == first, "switches back to window A");
        if (args.Contains("--live-keys"))
        {
            var slots = new WindowSlotTable();
            var notices = new List<WindowSwitchNotice>();
            using (var shortcuts = new WindowShortcuts(slots))
            {
                shortcuts.Notice += n => { lock (notices) notices.Add(n); };
                WindowShortcuts.Focus(first); Native.Pump(200);
                Native.Chord(0x5B, 0x10, '1');           // Win+Shift+1 registers A
                Native.Pump(400);
                Check(slots.Get(0)?.Handle == first, "Win+Shift+1 registered the focused window through the hook");
                WindowShortcuts.Focus(second); Native.Pump(200);
                Native.Chord(0x5B, '1');                 // Win+1 returns to A
                Native.Pump(500);
                Check(Native.Foreground() == first, "Win+1 switched to the registered window");
                Check(Native.ClassName(Native.Foreground()) != "Windows.UI.Core.CoreWindow", "Start menu did not open after the consumed chord");
                Native.Chord(0x5B, 'W');                 // empty slot shows a notice only
                Native.Pump(300);
                lock (notices) Check(notices.Any(n => n.Success && n.Key == 0) && notices.Any(n => !n.Success && n.Key == 5), "register and empty-slot notices raised");
                Check(Native.Foreground() == first, "empty slot leaves the current window in front");
                Native.Destroy(first); first = 0; Native.Pump(300);
                Check(slots.Get(0) is null, "closing the window clears its slot");
            }
        }
    }
    finally { if (first != 0) Native.Destroy(first); Native.Destroy(second); }
}
Console.WriteLine($"{checks} window switch checks passed.");

static class Native
{
    public static nint Create(string title)
    {
        nint window = CreateWindowEx(0, "STATIC", title, 0x10CF0000, 200, 200, 420, 200, 0, 0, 0, 0); // WS_OVERLAPPEDWINDOW | WS_VISIBLE
        if (window == 0) throw new InvalidOperationException("CreateWindowEx failed " + Marshal.GetLastWin32Error());
        return window;
    }
    public static void Destroy(nint window) { DestroyWindow(window); Pump(50); }
    public static nint Foreground() => GetForegroundWindow();
    public static string ClassName(nint window) { var name = new StringBuilder(256); GetClassName(window, name, 256); return name.ToString(); }
    public static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
            Thread.Sleep(10);
        }
    }
    // Presses the keys in order and releases them in reverse, as a person would.
    public static void Chord(params int[] keys)
    {
        var inputs = new List<Input>();
        foreach (int key in keys) inputs.Add(Key(key, false));
        foreach (int key in keys.Reverse()) inputs.Add(Key(key, true));
        foreach (var input in inputs) { SendInput(1, [input], Marshal.SizeOf<Input>()); Pump(25); }
    }
    private static Input Key(int key, bool up) => new() { Type = 1, Keyboard = new() { VirtualKey = (ushort)key, Flags = up ? 2u : 0 } };
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint W; public nint L; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string? className, string? title);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
