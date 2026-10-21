using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PersonalTools;

// Changes the Windows capture endpoint, so applications share the same mute state.
internal static class MicrophoneMute
{
    internal sealed record State(string Id, bool Muted);
    internal static State[] Read() => WithEndpoints(items => items.Select(x => new State(x.Id, GetMute(x.Volume))).ToArray());
    internal static bool Toggle() => WithEndpoints(items =>
    {
        if (items.Count == 0) throw new InvalidOperationException("연결된 마이크가 없습니다.");
        bool mute = !items.All(x => GetMute(x.Volume));
        foreach (var item in items) { item.Volume.SetMute(mute, Guid.Empty); }
        if (items.Any(x => GetMute(x.Volume) != mute)) throw new InvalidOperationException("일부 마이크의 음소거 상태를 확인하지 못했습니다.");
        return mute;
    });
    internal static void Restore(State[] states) => WithEndpoints(items =>
    {
        foreach (var item in items)
        {
            var state = states.FirstOrDefault(x => x.Id == item.Id);
            if (state is not null) item.Volume.SetMute(state.Muted, Guid.Empty);
        }
        return true;
    });
    private static bool GetMute(IEndpointVolume volume) { volume.GetMute(out bool muted); return muted; }
    private static T WithEndpoints<T>(Func<List<(string Id, IEndpointVolume Volume)>, T> action)
    {
        var enumerator = (IDeviceEnumerator)new DeviceEnumerator();
        IDeviceCollection? collection = null;
        var volumes = new List<(string Id, IEndpointVolume Volume)>();
        try
        {
            enumerator.EnumAudioEndpoints(1, 1, out collection); // Capture, DEVICE_STATE_ACTIVE.
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                try
                {
                    device.GetId(out string id);
                    Guid iid = typeof(IEndpointVolume).GUID;
                    device.Activate(ref iid, 23, 0, out var value);
                    volumes.Add((id, (IEndpointVolume)value));
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            return action(volumes);
        }
        finally
        {
            foreach (var item in volumes) Marshal.ReleaseComObject(item.Volume);
            if (collection is not null) Marshal.ReleaseComObject(collection);
            Marshal.ReleaseComObject(enumerator);
        }
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator { void EnumAudioEndpoints(int flow, uint mask, out IDeviceCollection devices); }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceCollection { void GetCount(out uint count); void Item(uint index, out IDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        void Activate(ref Guid iid, uint context, nint activation, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        void OpenPropertyStore(uint mode, out nint properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        void RegisterControlChangeNotify(nint callback);
        void UnregisterControlChangeNotify(nint callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, Guid context);
        void SetMasterVolumeLevelScalar(float level, Guid context);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, [In, MarshalAs(UnmanagedType.LPStruct)] Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

// A dedicated message thread keeps the hook responsive even while the UI is busy.
// Only Win+Alt+M is consumed; no key logging or changes to Game Bar settings.
internal sealed class MicrophoneHotkey : IDisposable
{
    private readonly Thread thread;
    private readonly Action<bool?, string?> report;
    private readonly HookProc callback;
    private readonly ManualResetEventSlim ready = new();
    private uint threadId;
    private nint hook;
    private Exception? startupError;
    private bool held;
    private readonly HashSet<int> modifiersDown = [];
    private static readonly int[] ModifierKeys = [0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
    private const uint ToggleMessage = 0x8000 + 77;
    private static void Status(string status)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalTools");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "microphone-shortcut-status.txt"), $"{DateTimeOffset.Now:O} {status}");
        }
        catch { }
    }
    internal MicrophoneHotkey(Action<bool?, string?> report)
    {
        this.report = report;
        callback = OnKey;
        thread = new Thread(Run) { IsBackground = true, Name = "Microphone mute shortcut" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (startupError is not null) { ready.Dispose(); throw startupError; }
    }
    private void Run()
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0);
        foreach (int key in ModifierKeys) if (Pressed(key)) modifiersDown.Add(key);
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == 0) startupError = new Win32Exception(Marshal.GetLastWin32Error());
        Status(hook == 0 ? $"Hook failed: {startupError}" : "Win+Alt+M hook ready");
        ready.Set();
        if (hook == 0) return;
        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == ToggleMessage)
                {
                    try { bool muted = MicrophoneMute.Toggle(); Status($"Windows microphones muted={muted}"); report(muted, null); }
                    catch (Exception error) { Status($"Toggle failed: {error}"); report(null, error.Message); }
                }
                else { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
        }
        finally { UnhookWindowsHookEx(hook); }
    }
    private nint OnKey(int code, nuint message, nint data)
    {
        if (code >= 0)
        {
            int key = Marshal.ReadInt32(data);
            bool down = message is 0x100 or 0x104;
            bool up = message is 0x101 or 0x105;
            // The low-level callback runs before Windows updates async key state.
            // Track modifier events so fast chords do not depend on that timing.
            if (ModifierKeys.Contains(key))
            {
                if (down) modifiersDown.Add(key);
                if (up) modifiersDown.Remove(key);
            }
            if (key == 0x4D)
            {
                if (held && up) { held = false; return 1; }
                if (held && down) return 1;
                bool win = modifiersDown.Contains(0x5B) || modifiersDown.Contains(0x5C);
                bool alt = modifiersDown.Contains(0xA4) || modifiersDown.Contains(0xA5);
                bool extra = modifiersDown.Overlaps([0xA0, 0xA1, 0xA2, 0xA3]);
                if (down && win && alt && !extra)
                {
                    held = true;
                    PostThreadMessage(threadId, ToggleMessage, 0, 0);
                    return 1;
                }
            }
        }
        return CallNextHookEx(0, code, message, data);
    }
    private static bool Pressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public void Dispose()
    {
        PostThreadMessage(threadId, 0x12, 0, 0);
        thread.Join();
        ready.Dispose();
    }
    private delegate nint HookProc(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint W; public nint L; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nuint w, nint l);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}
