using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PersonalTools;

// Key is the slot index shown as keycaps before KeyText; -1 shows Title alone.
public sealed record WindowSwitchNotice(string Title, string Detail, WindowSlot? Slot, bool Success, int Key = -1, string KeyText = "");

// Owns the low-level keyboard hook and the window close/rename hooks on one
// message thread. Only Win(+Shift)+1-4/Q/W/E/R are consumed; other keys pass
// through untouched and nothing is recorded.
internal sealed class WindowShortcuts : IDisposable
{
    private const uint ActionMessage = 0x8000 + 81;
    private const nuint MaskSignature = 0x50545753; // marks our own injected mask key
    private static readonly int[] ModifierKeys = [0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
    private static readonly HashSet<string> ShellClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "Shell_InputSwitchTopLevelWindow"];
    private readonly WindowSlotTable slots;
    private readonly Thread thread;
    private readonly HookProc keyboardProc;
    private readonly WinEventProc eventProc;
    private readonly ManualResetEventSlim ready = new();
    private readonly HashSet<int> modifiersDown = [];
    private readonly HashSet<int> consumed = [];
    private uint threadId;
    private nint keyboardHook, destroyHook, nameHook;
    private Exception? startupError;
    public event Action? Changed;
    public event Action<WindowSwitchNotice>? Notice;

    public WindowShortcuts(WindowSlotTable slots)
    {
        this.slots = slots;
        keyboardProc = OnKey;
        eventProc = OnWindowEvent;
        thread = new Thread(Run) { IsBackground = true, Name = "Window quick switch" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (startupError is not null) { thread.Join(); ready.Dispose(); throw startupError; }
    }
    private void Run()
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0);
        foreach (int key in ModifierKeys) if ((GetAsyncKeyState(key) & 0x8000) != 0) modifiersDown.Add(key);
        keyboardHook = SetWindowsHookEx(13, keyboardProc, GetModuleHandle(null), 0);
        if (keyboardHook == 0) startupError = new Win32Exception(Marshal.GetLastWin32Error(), "창 빠른 전환 단축키를 등록하지 못했습니다.");
        else
        {
            destroyHook = SetWinEventHook(0x8001, 0x8001, 0, eventProc, 0, 0, 0);
            nameHook = SetWinEventHook(0x800C, 0x800C, 0, eventProc, 0, 0, 0);
        }
        ready.Set();
        if (keyboardHook == 0) return;
        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == ActionMessage)
                {
                    try { Perform((int)message.W, message.L != 0); }
                    catch (Exception error) { Notice?.Invoke(new("창 빠른 전환을 처리하지 못했습니다", error.Message, null, false)); }
                }
                else { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
        }
        finally
        {
            UnhookWindowsHookEx(keyboardHook);
            if (destroyHook != 0) UnhookWinEvent(destroyHook);
            if (nameHook != 0) UnhookWinEvent(nameHook);
        }
    }
    private nint OnKey(int code, nuint message, nint data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KeyboardInfo>(data);
            int key = (int)info.VirtualKey;
            bool down = message is 0x100 or 0x104, up = message is 0x101 or 0x105;
            // The callback runs before Windows updates async key state, so track modifiers here.
            if (ModifierKeys.Contains(key))
            {
                if (down) modifiersDown.Add(key);
                if (up) modifiersDown.Remove(key);
            }
            else if (info.ExtraInfo != MaskSignature)
            {
                if (up && consumed.Remove(key)) return 1;
                if (down && consumed.Contains(key)) return 1; // auto-repeat of a consumed chord
                if (down)
                {
                    var (action, slot) = WindowSlotKeys.Classify(key,
                        modifiersDown.Contains(0x5B) || modifiersDown.Contains(0x5C),
                        modifiersDown.Contains(0xA0) || modifiersDown.Contains(0xA1),
                        modifiersDown.Contains(0xA2) || modifiersDown.Contains(0xA3),
                        modifiersDown.Contains(0xA4) || modifiersDown.Contains(0xA5));
                    if (action != WindowKeyAction.None)
                    {
                        consumed.Add(key);
                        PostThreadMessage(threadId, ActionMessage, (nuint)slot, action == WindowKeyAction.Register ? 1 : 0);
                        return 1;
                    }
                }
            }
        }
        return CallNextHookEx(0, code, message, data);
    }
    private void Perform(int slot, bool register)
    {
        // Windows opens Start when Win is released with no other key in between.
        // An unassigned key (0xE8) tells the shell the Win key was used as a modifier.
        Input Key(bool up) => new() { Type = 1, Keyboard = new() { VirtualKey = 0xE8, Flags = up ? 2u : 0, ExtraInfo = MaskSignature } };
        SendInput(2, [Key(false), Key(true)], Marshal.SizeOf<Input>());
        char key = WindowSlotKeys.Keys[slot];
        if (register)
        {
            var window = Describe(GetForegroundWindow());
            if (window is null) { Notice?.Invoke(new("등록할 창을 먼저 선택해 주세요", $"등록할 창을 클릭한 뒤 Win+Shift+{key}를 누르세요.", null, false)); return; }
            slots.Assign(slot, window);
            Changed?.Invoke();
            Notice?.Invoke(new($"Win+{key} 키에 등록했습니다", window.Title.Length > 0 ? window.Title : window.AppName, window, true, slot, "키에 등록했습니다"));
            return;
        }
        var target = slots.Get(slot);
        if (target is null) { Notice?.Invoke(new($"Win+{key} 키는 비어 있습니다", $"등록할 창에서 Win+Shift+{key}를 누르세요.", null, false, slot, "키는 비어 있습니다")); return; }
        if (!IsAlive(target))
        {
            slots.ClearHandle(target.Handle);
            Changed?.Invoke();
            Notice?.Invoke(new($"Win+{key} 창이 닫혀 있습니다", "등록을 해제했습니다. 열려 있는 창에서 다시 등록해 주세요.", null, false));
            return;
        }
        if (!Focus(target.Handle))
        {
            var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = target.Handle, Flags = 3 | 12, Count = 3 };
            FlashWindowEx(ref flash);
            Notice?.Invoke(new("창을 앞으로 가져오지 못했습니다", "작업 표시줄에서 깜박이는 창을 선택해 주세요.", target, false));
        }
    }
    private void OnWindowEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != 0 || childId != 0 || window == 0 || !slots.Contains(window)) return;
        if (eventType == 0x8001 ? slots.ClearHandle(window).Length > 0 : slots.UpdateTitle(window, WindowTitle(window))) Changed?.Invoke();
    }
    internal static bool IsAlive(WindowSlot slot) =>
        IsWindow(slot.Handle) && GetWindowThreadProcessId(slot.Handle, out uint process) == slot.ThreadId && process == slot.ProcessId;
    internal static bool Focus(nint window)
    {
        if (IsIconic(window)) ShowWindow(window, 9);
        else if (!IsWindowVisible(window)) ShowWindow(window, 5);
        if (SetForegroundWindow(window) && GetForegroundWindow() == window) return true;
        // Windows limits which process may take the foreground. Sharing the current
        // foreground thread's input state is the documented way around that limit.
        nint current = GetForegroundWindow();
        uint foregroundThread = current == 0 ? 0 : GetWindowThreadProcessId(current, out _), self = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(self, foregroundThread, false); }
        if (GetForegroundWindow() == window) return true;
        SwitchToThisWindow(window, true);
        for (int i = 0; i < 10 && GetForegroundWindow() != window; i++) Thread.Sleep(10);
        return GetForegroundWindow() == window;
    }
    internal static WindowSlot? Describe(nint window)
    {
        if (window == 0) return null;
        window = GetAncestor(window, 2);
        if (window == 0 || !IsWindowVisible(window) || ShellClasses.Contains(ClassName(window))) return null;
        long style = GetWindowLongPtr(window, -20).ToInt64();
        if ((style & 0x80) != 0 && (style & 0x40000) == 0) return null; // tool windows and our own panels
        uint thread = GetWindowThreadProcessId(window, out uint process);
        if (thread == 0) return null;
        uint appProcess = process;
        // Store apps are hosted by ApplicationFrameHost; name them after the hosted app.
        if (ClassName(window) == "ApplicationFrameWindow")
        {
            EnumChildWindows(window, (child, _) =>
            {
                if (ClassName(child) != "Windows.UI.Core.CoreWindow") return true;
                GetWindowThreadProcessId(child, out uint hosted);
                if (hosted != process) { appProcess = hosted; return false; }
                return true;
            }, 0);
        }
        string? path = ProcessPath(appProcess);
        return new(window, process, thread, WindowTitle(window), AppName(path, appProcess), path);
    }
    internal static string WindowTitle(nint window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var text = new StringBuilder(length + 1);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }
    private static string ClassName(nint window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }
    private static string? ProcessPath(uint process)
    {
        nint handle = OpenProcess(0x1000, false, process);
        if (handle == 0) return null;
        try
        {
            var path = new StringBuilder(1024); int size = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
    private static string AppName(string? path, uint process)
    {
        if (path is not null)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(version.FileDescription)) return version.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(version.ProductName)) return version.ProductName.Trim();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return Path.GetFileNameWithoutExtension(path);
        }
        try { using var p = Process.GetProcessById((int)process); return p.ProcessName; }
        catch (ArgumentException) { return "앱"; }
    }
    public void Dispose()
    {
        PostThreadMessage(threadId, 0x12, 0, 0);
        thread.Join();
        ready.Dispose();
    }
    private delegate nint HookProc(int code, nuint message, nint data);
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumProc(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInfo { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public nint Window; public uint Flags, Count, Timeout; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint W; public nint L; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nuint w, nint l);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(nint window, bool altTab);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool enable);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);
}
