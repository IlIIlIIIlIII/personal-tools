using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PersonalTools;

internal sealed class HdrDisplay : IHdrDisplay
{
    public HdrMonitor[] Read() => Enumerate().Select(x => x.Monitor).ToArray();
    public void Set(string id, bool enabled)
    {
        var target = Enumerate().FirstOrDefault(x => x.Monitor.Id == id)
            ?? throw new IOException("모니터 연결이 변경되었습니다. 새로 고친 뒤 다시 시도하세요.");
        if (!target.Monitor.Supported || target.Monitor.Error is not null)
            throw new IOException(target.Monitor.Error ?? "HDR을 지원하지 않는 모니터입니다.");
        if (target.Monitor.Enabled == enabled) return;
        var packet = new SetColor { Header = Header.For<SetColor>(target.Modern ? 16u : 10u, target.Path), Value = enabled ? 1u : 0u };
        Check(SetDeviceInfo(ref packet));
        for (int retry = 0; retry < 10; retry++)
        {
            Thread.Sleep(150);
            var actual = Read().FirstOrDefault(m => m.Id == id);
            if (actual is { Error: null } && actual.Enabled == enabled) return;
        }
        throw new IOException("HDR 적용 결과를 확인하지 못했습니다. Windows 디스플레이 설정을 확인하세요.");
    }

    private sealed record Target(PathInfo Path, HdrMonitor Monitor, bool Modern);
    private static List<Target> Enumerate()
    {
        PathInfo[] paths = [];
        ModeInfo[] modes = [];
        uint pathCount = 0, modeCount = 0;
        for (int attempt = 0; ; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(2, out pathCount, out modeCount));
            paths = new PathInfo[pathCount]; modes = new ModeInfo[modeCount];
            int result = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0);
            if (result == 122 && attempt < 4) continue;
            Check(result); break;
        }
        var resultList = new List<Target>();
        foreach (var path in paths.Take((int)pathCount))
        {
            var name = new TargetName { Header = Header.For<TargetName>(2, path) };
            if (GetTargetName(ref name) != 0 || string.IsNullOrWhiteSpace(name.DevicePath)) continue;
            bool primary = path.Source.ModeIndex < modeCount && modes[path.Source.ModeIndex].Type == 1 &&
                modes[path.Source.ModeIndex].X == 0 && modes[path.Source.ModeIndex].Y == 0;
            var color = new Color2 { Header = Header.For<Color2>(15, path) };
            int error = GetColor2(ref color);
            bool modern = error == 0, supported = false, enabled = false;
            string? message = null;
            if (modern)
            {
                supported = (color.Flags & 16) != 0;
                enabled = (color.Flags & 32) != 0;
                if ((color.Flags & 8) != 0) message = "Windows 정책 또는 전원 상태가 HDR을 제한합니다.";
            }
            else if (error is 87 or 50)
            {
                var old = new Color1 { Header = Header.For<Color1>(9, path) };
                error = GetColor1(ref old);
                // On older Windows, Advanced Color also covers SDR color management.
                // Require PQ HDR metadata from EDID; never treat wide-color alone as HDR.
                supported = error == 0 && (old.Flags & 1) != 0 && (old.Flags & 4) == 0 && SupportsPq(name.DevicePath);
                enabled = supported && (old.Flags & 2) != 0;
                if (error != 0) message = new Win32Exception(error).Message;
                else if ((old.Flags & 8) != 0) message = "Windows에서 HDR을 제한하고 있습니다.";
            }
            else message = new Win32Exception(error).Message;
            // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL, DISPLAYPORT_EMBEDDED or UDI_EMBEDDED: a laptop panel.
            bool builtIn = path.Target.Technology is 0x80000000u or 11 or 13;
            var label = string.IsNullOrWhiteSpace(name.FriendlyName) ? builtIn ? "내장 디스플레이" : "디스플레이" : name.FriendlyName;
            resultList.Add(new(path, new(name.DevicePath, label, primary, supported, enabled, message, builtIn), modern));
        }
        var unique = resultList.DistinctBy(x => x.Monitor.Id).ToList();
        // Same-model monitors keep their source number so they stay distinguishable.
        var repeated = unique.GroupBy(x => x.Monitor.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return unique.Select(x => repeated.Contains(x.Monitor.Name)
            ? x with { Monitor = x.Monitor with { Name = $"{x.Monitor.Name} · {x.Path.Source.Id + 1}" } } : x).ToList();
    }

    private static bool SupportsPq(string devicePath)
    {
        try
        {
            var parts = devicePath.Split('#');
            if (parts.Length < 3 || !parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) return false;
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            return key?.GetValue("EDID") is byte[] edid && HasPqMetadata(edid);
        }
        catch { return false; }
    }
    internal static bool HasPqMetadata(byte[] edid)
    {
        if (edid.Length < 128) return false;
        for (int block = 1; block <= edid[126] && (block + 1) * 128 <= edid.Length; block++)
        {
            int offset = block * 128;
            if (edid[offset] != 2) continue;
            int end = edid[offset + 2];
            if (end < 4 || end > 127) continue;
            for (int i = 4; i < end;)
            {
                int tag = edid[offset + i] >> 5, length = edid[offset + i] & 31;
                if (i + length >= end) break;
                if (tag == 7 && length >= 3 && edid[offset + i + 1] == 6 && (edid[offset + i + 2] & 4) != 0) return true;
                i += length + 1;
            }
        }
        return false;
    }

    private static void Check(int result) { if (result != 0) throw new Win32Exception(result); }
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] internal struct Source { public Luid Adapter; public uint Id, ModeIndex, Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct TargetInfo
    {
        public Luid Adapter; public uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLine;
        public int Available; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { public Source Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct ModeInfo
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(28)] public int X;
        [FieldOffset(32)] public int Y;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Header
    {
        public uint Type, Size; public Luid Adapter; public uint Id;
        public static Header For<T>(uint type, PathInfo path) => new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), Adapter = path.Target.Adapter, Id = path.Target.Id };
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct TargetName
    {
        public Header Header; public uint Flags, Technology; public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Color1 { public Header Header; public uint Flags, Encoding, Bits; }
    [StructLayout(LayoutKind.Sequential)] internal struct Color2 { public Header Header; public uint Flags, Encoding, Bits, Mode; }
    [StructLayout(LayoutKind.Sequential)] internal struct SetColor { public Header Header; public uint Value; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] path, ref uint modes, [Out] ModeInfo[] mode, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetTargetName(ref TargetName value);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetColor1(ref Color1 value);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetColor2(ref Color2 value);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] private static extern int SetDeviceInfo(ref SetColor value);
}
