using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PersonalTools;

internal sealed class ClipboardNative : IDisposable
{
    private readonly nint window;
    private readonly SubclassProc callback;
    private readonly Action changed, hotkey;
    private bool registered;
    public ClipboardNative(nint window, Action changed, Action hotkey)
    {
        this.window = window; this.changed = changed; this.hotkey = hotkey;
        callback = WindowMessage;
        if (!SetWindowSubclass(window, callback, 2, 0)) throw new InvalidOperationException("클립보드 창 연결에 실패했습니다.");
        if (!AddClipboardFormatListener(window))
        { RemoveWindowSubclass(window, callback, 2); throw new InvalidOperationException("클립보드 감지를 시작하지 못했습니다."); }
    }
    public void SetHotkey(uint modifiers, uint key)
    {
        if (registered) UnregisterHotKey(window, 0x5043);
        registered = RegisterHotKey(window, 0x5043, modifiers | 0x4000, key);
        if (!registered) throw new InvalidOperationException("단축키가 다른 앱에서 사용 중입니다. 클립보드 설정에서 변경해 주세요.");
    }
    private nint WindowMessage(nint hwnd, uint message, nuint w, nint l, nuint id, nuint data)
    {
        if (message == 0x31D) changed();
        if (message == 0x312 && w == 0x5043) { hotkey(); return 0; }
        return DefSubclassProc(hwnd, message, w, l);
    }
    public static string SourceApp()
    {
        GetWindowThreadProcessId(GetClipboardOwner(), out uint id);
        try { return id == 0 ? "알 수 없음" : Process.GetProcessById((int)id).ProcessName + ".exe"; }
        catch { return "알 수 없음"; }
    }
    public static string? SourcePath()
    {
        GetWindowThreadProcessId(GetClipboardOwner(), out uint id);
        try { using var process = Process.GetProcessById((int)id); return process.MainModule?.FileName; }
        catch { return null; }
    }
    // Windows ignores SetForegroundWindow from a process that is not foreground (the
    // foreground lock), so a panel opened by the hotkey can appear without keyboard focus.
    // Attaching to the foreground thread's input queue lifts the lock for this call.
    public static bool ForceForeground(nint window)
    {
        if (window == 0 || !IsWindow(window)) return false;
        if (GetForegroundWindow() == window || SetForegroundWindow(window) && GetForegroundWindow() == window) return true;
        uint self = GetCurrentThreadId(), owner = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = owner != 0 && owner != self && AttachThreadInput(self, owner, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(self, owner, false); }
        return GetForegroundWindow() == window;
    }
    // A dialog or popup owned by the target counts; it is where the target's focus lives.
    private static bool IsForeground(nint target)
    {
        nint current = GetForegroundWindow();
        return current != 0 && (current == target || GetAncestor(current, 3) == GetAncestor(target, 3));
    }
    private static bool ModifierHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    private static bool HasFocus(nint target)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(target, out _), ref info) && info.Focus != 0;
    }
    // Clipboard listeners (history, sync tools) open the clipboard right after a copy;
    // most apps silently drop a paste that cannot open it.
    private static bool ClipboardFree()
    {
        if (!OpenClipboard(0)) return false;
        CloseClipboard(); return true;
    }
    // The caller returns the foreground to the target before hiding the panel; this waits for
    // the switch to land. Never paste into a different window or while a modifier is held.
    public static async Task PasteAsync(nint target)
    {
        const string manual = " 클립보드에 복사해 두었으니 대상 앱에서 Ctrl+V를 눌러 주세요.";
        if (target == 0 || !IsWindow(target)) throw new InvalidOperationException("붙여넣을 창이 닫혔습니다." + manual);
        for (int i = 0; i < 30 && !IsForeground(target); i++)
        {
            if (i == 10) ForceForeground(target);
            await Task.Delay(10);
        }
        if (!IsForeground(target)) throw new InvalidOperationException("붙여넣을 창을 다시 활성화하지 못했습니다." + manual);
        // Activation reaches the target's thread asynchronously; keys that arrive before it
        // restores its focused control are dropped.
        for (int i = 0; i < 20 && !HasFocus(target); i++) await Task.Delay(10);
        await Task.Delay(40);
        for (int i = 0; i < 25 && !ClipboardFree(); i++) await Task.Delay(20);
        for (int i = 0; i < 50 && ModifierHeld(); i++) await Task.Delay(20);
        if (ModifierHeld()) throw new InvalidOperationException("Ctrl, Shift, Alt 또는 Windows 키가 눌려 있어 붙여넣지 않았습니다." + manual);
        if (!IsForeground(target)) throw new InvalidOperationException("붙여넣는 중에 다른 창이 활성화되었습니다." + manual);
        Input Key(ushort code, bool up) => new() { Type = 1, Keyboard = new() { VirtualKey = code, Flags = up ? 2u : 0 } };
        Input[] inputs = [Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true)];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return;
        // Balance modifiers if Windows accepted only part of the sequence.
        SendInput(2, [Key(0x56, true), Key(0x11, true)], Marshal.SizeOf<Input>());
        throw new InvalidOperationException("Ctrl+V 입력을 보내지 못했습니다. 관리자 권한으로 실행 중인 앱에는 자동으로 붙여넣을 수 없습니다." + manual);
    }
    public void Dispose()
    {
        if (registered) UnregisterHotKey(window, 0x5043);
        RemoveClipboardFormatListener(window);
        RemoveWindowSubclass(window, callback, 2);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo { public int Size; public uint Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyboardInput Keyboard; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint SubclassProc(nint h, uint m, nuint w, nint l, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint h, SubclassProc p, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint h, SubclassProc p, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint h, uint m, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(nint h);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(nint h);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint h);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool enable);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
