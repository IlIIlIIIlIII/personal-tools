using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class HotkeyLiveChecks
{
    private delegate bool EnumProc(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint W; public nint L; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ushort Key; [FieldOffset(12)] public uint Flags; }

    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            while (PeekMessage(out var message, 0, 0, 0, 1)) DispatchMessage(ref message);
            Thread.Sleep(10);
        }
    }
    private static void Shortcut()
    {
        Input Key(ushort key, bool up = false) => new() { Type = 1, Key = key, Flags = up ? 2u : 0u };
        Input[] keys = [Key(0x11), Key(0x10), Key(0x56), Key(0x56, true), Key(0x10, true), Key(0x11, true)];
        if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>()) != keys.Length) throw new Exception("Shortcut input failed");
    }
    internal static void Run()
    {
        using var app = Process.GetProcessesByName("PersonalToolsWindows").Single();
        nint Panel()
        {
            nint result = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out uint pid);
                if (pid != app.Id) return true;
                var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
                if (title.ToString() == "클립보드 · Personal Tools") result = window;
                return true;
            }, 0);
            return result;
        }
        nint previous = GetForegroundWindow();
        nint fixture = CreateWindowEx(0, "STATIC", "Personal Tools hotkey verification", 0x10CF0000, 100, 100, 460, 160, 0, 0, 0, 0);
        if (fixture == 0) throw new Exception("Test window creation failed");
        try
        {
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                SetForegroundWindow(fixture); Pump(400);
                if (GetForegroundWindow() != fixture) throw new Exception("Test window did not receive foreground");
                Shortcut(); Pump(3000);
                nint panel = Panel();
                bool visible = panel != 0 && IsWindowVisible(panel), focused = panel != 0 && GetForegroundWindow() == panel;
                Console.WriteLine($"Open {attempt}: visible={visible}, foreground={focused}");
                if (!visible || !focused) throw new Exception("A single Ctrl+Shift+V did not open and focus the panel");
                if (attempt <= 3) Shortcut();
                else SetForegroundWindow(fixture);
                Pump(300);
                if (IsWindowVisible(panel)) throw new Exception("Toggle or focus-loss dismissal failed");
            }
        }
        finally { SetForegroundWindow(previous); DestroyWindow(fixture); }
        Console.WriteLine("PASS: six single-press opens after toggle and focus-loss dismissals");
    }
}
