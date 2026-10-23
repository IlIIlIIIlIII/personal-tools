using System.Runtime.InteropServices;

namespace PersonalTools;

// A monitor this app turned off. Id is the device path, which stays the same while the monitor is
// plugged into the same port; Name is the label the page shows (DisplayInfo.Label).
internal sealed record DisplayTarget(string Id, string Name);

// Label tells same-model monitors apart with Windows' display number, as the auto-HDR page does.
internal sealed record DisplayInfo(string Id, string Name, string Label, uint Technology, bool Primary, int X, int Y, int Width, int Height)
{
    // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL, DISPLAYPORT_EMBEDDED or UDI_EMBEDDED: a laptop panel, which has no DDC/CI.
    public bool Internal => Technology is 0x80000000u or 11 or 13;
}

internal interface IDisplayPowerControl
{
    DisplayInfo[] List();
    // MCCS power mode (VCP D6): 1 on, 4 off; null when the monitor did not answer.
    uint? Read(string id);
    bool Set(string id, bool on);
}

// Switches the panel itself over DDC/CI. Windows keeps the display attached and keeps rendering
// to it, so window layout and applications do not change. Checked on the Gigabyte MO32U24
// (2026-10-23): D6=5 turns the panel off, it then reads back 4 and keeps answering DDC/CI,
// and D6=1 turns it back on in about two seconds.
internal sealed class DisplayDdc : IDisplayPowerControl
{
    private const byte PowerMode = 0xD6;
    private const uint On = 1, Off = 5;

    public DisplayInfo[] List()
    {
        var monitors = Attached();
        var repeated = monitors.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return monitors.Select(m => new DisplayInfo(m.Id, m.Name,
            m.Internal ? "내장 디스플레이" : repeated.Contains(m.Name) ? $"{m.Name} · {m.Source + 1}" : m.Name,
            m.Technology, m.Primary, m.Bounds.Left, m.Bounds.Top, m.Bounds.Right - m.Bounds.Left, m.Bounds.Bottom - m.Bounds.Top)).ToArray();
    }
    public uint? Read(string id) =>
        WithMonitor(id, handle => GetVCPFeatureAndVCPFeatureReply(handle, PowerMode, out _, out uint value, out _) ? value : (uint?)null, null);
    public bool Set(string id, bool on) => WithMonitor(id, handle => SetVCPFeature(handle, PowerMode, on ? On : Off), false);

    private sealed record Monitor(nint Handle, string Id, string Name, uint Technology, uint Source, bool Primary, Rect Bounds)
    {
        public bool Internal => Technology is 0x80000000u or 11 or 13;
    }
    private static List<Monitor> Attached()
    {
        // GDI source name (\\.\DISPLAYn) → the monitor shown on it.
        var targets = new Dictionary<string, (string Id, string Name, uint Technology, uint Source)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in HdrDisplay.QueryActive(out _))
        {
            var source = new SourceName { Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = path.Source.Adapter, Id = path.Source.Id } };
            if (GetSourceName(ref source) != 0 || !HdrDisplay.TryTargetName(path, out var name)) continue;
            targets.TryAdd(source.GdiName, (name.DevicePath, name.FriendlyName, path.Target.Technology, path.Source.Id));
        }
        var monitors = new List<Monitor>();
        EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(handle, ref info) && targets.TryGetValue(info.Device, out var target))
                monitors.Add(new(handle, target.Id, target.Name, target.Technology, target.Source, (info.Flags & 1) != 0, info.Monitor));
            return true;
        }, 0);
        return monitors;
    }
    private static T WithMonitor<T>(string id, Func<nint, T> action, T missing)
    {
        var monitor = Attached().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        if (monitor is null || !GetNumberOfPhysicalMonitorsFromHMONITOR(monitor.Handle, out uint count) || count == 0) return missing;
        var physical = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor.Handle, count, physical)) return missing;
        try { return action(physical[0].Handle); }
        finally { DestroyPhysicalMonitors(count, physical); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
    {
        public HdrDisplay.Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PhysicalMonitor
    {
        public nint Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref SourceName value);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] private static extern bool GetVCPFeatureAndVCPFeatureReply(nint monitor, byte code, out uint type, out uint current, out uint maximum);
    [DllImport("dxva2.dll")] private static extern bool SetVCPFeature(nint monitor, byte code, uint value);
}
