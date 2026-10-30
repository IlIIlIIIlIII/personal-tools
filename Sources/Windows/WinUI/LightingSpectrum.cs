using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace PersonalTools;

// The laptop's own lighting controller: ITE 048D:C909 (Legion 9 18IAX10), HID collection
// FF89:0007 with one 960-byte feature report (ID 7) that Lenovo calls "Spectrum". A request is
// written with SetFeature; its answer is read back with GetFeature. Measured on this laptop
// (2026-10-30, docs/windows-lighting.md):
// - the first read after a request usually still holds the previous answer, so every answer is
//   checked by its opcode (and profile) and read again until it matches;
// - about 150 ms after an answer the report falls back to the live LED colors (opcode 03), which is
//   also how "idle" is recognised before the next request;
// - writes (brightness, logo, profile, effects) are not answered; the controller ignores requests
//   for a short while after an effect write, which is stored in the firmware.

public enum LightingZone { Keyboard, Bar, Logo }

public enum LightingEffectKind : byte
{
    ScrewRainbow = 1, RainbowWave = 2, ColorChange = 3, ColorPulse = 4, ColorWave = 5, Smooth = 6, Rain = 7,
    Ripple = 8, AudioBounce = 9, AudioRipple = 10, Static = 11, TypeLighting = 12, AuraSync = 13,
}

public enum LightingColorMode : byte { None = 0, Random = 1, List = 2 }

public readonly record struct LightingColor(byte R, byte G, byte B)
{
    public static readonly LightingColor Black = new(0, 0, 0);
    public static readonly LightingColor White = new(255, 255, 255);
    // Lenovo's factory blue (profile 2).
    public static readonly LightingColor LegionBlue = new(0x15, 0x8D, 0xDD);
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public static LightingColor Parse(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) throw new FormatException("색은 #RRGGBB 형식이어야 합니다: " + hex);
        return new(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
    }
    public override string ToString() => Hex;
}

// One effect of a profile, as the firmware stores it. Speed 1..3, Clockwise 1..2 and Direction
// 1..4 are 0 when the effect has no such setting.
public sealed record SpectrumEffect(LightingEffectKind Kind, byte Speed, byte Clockwise, byte Direction,
    LightingColorMode ColorMode, LightingColor[] Colors, ushort[] Keys)
{
    public bool Equivalent(SpectrumEffect other) => Kind == other.Kind && Speed == other.Speed && Clockwise == other.Clockwise &&
        Direction == other.Direction && ColorMode == other.ColorMode && Colors.SequenceEqual(other.Colors) && Keys.SequenceEqual(other.Keys);
}

public static class LightingEffects
{
    // Which settings each effect takes, as the firmware lists them (request C6, 13 effects:
    // 02 speed, 03 rotation, 04 direction, 05 color mode).
    public static bool HasSpeed(LightingEffectKind kind) => kind is not (LightingEffectKind.Static or LightingEffectKind.AudioBounce
        or LightingEffectKind.AudioRipple or LightingEffectKind.AuraSync);
    public static bool HasColor(LightingEffectKind kind) => kind is LightingEffectKind.Static or LightingEffectKind.ColorChange
        or LightingEffectKind.ColorPulse or LightingEffectKind.ColorWave or LightingEffectKind.Smooth or LightingEffectKind.Rain
        or LightingEffectKind.Ripple or LightingEffectKind.TypeLighting;
    public static bool HasDirection(LightingEffectKind kind) => kind is LightingEffectKind.RainbowWave or LightingEffectKind.ColorWave;
    public static bool HasRotation(LightingEffectKind kind) => kind == LightingEffectKind.ScrewRainbow;
    // Audio and screen effects take every light and need Lenovo's own app to feed them.
    public static bool NeedsLenovoApp(LightingEffectKind kind) => kind is LightingEffectKind.AudioBounce
        or LightingEffectKind.AudioRipple or LightingEffectKind.AuraSync;

    public static readonly LightingEffectKind[] KeyboardChoices =
    [
        LightingEffectKind.Static, LightingEffectKind.ColorPulse, LightingEffectKind.ColorChange, LightingEffectKind.ColorWave,
        LightingEffectKind.RainbowWave, LightingEffectKind.ScrewRainbow, LightingEffectKind.Smooth, LightingEffectKind.Rain,
        LightingEffectKind.Ripple, LightingEffectKind.TypeLighting,
    ];
    public static readonly LightingEffectKind[] ZoneChoices =
    [
        LightingEffectKind.Static, LightingEffectKind.ColorPulse, LightingEffectKind.ColorChange, LightingEffectKind.ColorWave,
        LightingEffectKind.RainbowWave, LightingEffectKind.ScrewRainbow, LightingEffectKind.Smooth,
    ];
    public static LightingEffectKind[] Choices(LightingZone zone) => zone == LightingZone.Keyboard ? KeyboardChoices : ZoneChoices;

    public static string Name(LightingEffectKind kind) => kind switch
    {
        LightingEffectKind.ScrewRainbow => "나선 무지개",
        LightingEffectKind.RainbowWave => "무지개 물결",
        LightingEffectKind.ColorChange => "색 바뀜",
        LightingEffectKind.ColorPulse => "숨쉬기",
        LightingEffectKind.ColorWave => "색 물결",
        LightingEffectKind.Smooth => "부드럽게 바뀜",
        LightingEffectKind.Rain => "빗방울",
        LightingEffectKind.Ripple => "물결 퍼짐",
        LightingEffectKind.AudioBounce => "소리 반응",
        LightingEffectKind.AudioRipple => "소리 물결",
        LightingEffectKind.Static => "고정",
        LightingEffectKind.TypeLighting => "타자 반응",
        LightingEffectKind.AuraSync => "화면 색 따라가기",
        _ => $"효과 {(byte)kind}",
    };
}

public static class SpectrumCodes
{
    // The "every light" code that audio and screen effects use instead of a key list.
    public const ushort AllLights = 0x65;
    public const ushort Logo = 0x5DD;
    // The high byte of a key code is its zone: 0 keys, 1 the front light bar, 3 vents (listed by
    // the firmware but not fitted on the 18IAX10, they always read black), 5 the lid logo.
    public static LightingZone? ZoneOf(ushort code) => code == 0 || code == AllLights ? null : (code >> 8) switch
    {
        0 => LightingZone.Keyboard,
        1 => LightingZone.Bar,
        5 => LightingZone.Logo,
        _ => null,
    };
    public static bool Covers(SpectrumEffect effect, LightingZone zone) =>
        effect.Keys.Any(k => k == AllLights || ZoneOf(k) == zone);
}

// The key matrix the firmware reports (C4/C5): rows of key codes, wider keys repeating their code,
// plus a second short row that holds the logo. Used for the zone key lists and the page's preview.
public sealed record SpectrumLayout(ushort[][] Rows, ushort[] Extra)
{
    public IEnumerable<ushort> Codes => Rows.SelectMany(r => r).Concat(Extra).Where(c => c != 0).Distinct();
    public ushort[] Keys(LightingZone zone) => Codes.Where(c => SpectrumCodes.ZoneOf(c) == zone).OrderBy(c => c).ToArray();
}

public static class SpectrumPacket
{
    public const int Length = 960;
    public const byte ReportId = 7;
    public const byte State = 0x03;
    public const byte KeyCount = 0xC4, KeyPage = 0xC5, ProfileChange = 0xC8, ProfileDefault = 0xC9, Profile = 0xCA,
        EffectChange = 0xCB, Effect = 0xCC, GetBrightness = 0xCD, Brightness = 0xCE, GetLogo = 0xA5, Logo = 0xA6;
    public const int MaxBrightness = 9, Profiles = 6;

    public static byte[] Request(byte op, params byte[] args)
    {
        var report = new byte[Length];
        report[0] = ReportId;
        report[1] = op;
        report[2] = 0xC0;
        report[3] = 0x03;
        args.CopyTo(report, 4);
        return report;
    }

    // C8/CB requests on Gen 10 carry the real payload length as a little-endian u16 in bytes 2..3
    // (Lenovo Legion Toolkit 4646946, measured working here); the profile and two fixed 01 bytes
    // come first, then the effects.
    public static byte[] EffectRequest(int profile, IReadOnlyList<SpectrumEffect> effects)
    {
        if (profile is < 1 or > Profiles) throw new ArgumentOutOfRangeException(nameof(profile));
        var body = Encode(effects);
        var report = new byte[Length];
        report[0] = ReportId;
        report[1] = EffectChange;
        report[4] = (byte)profile;
        report[5] = 1;
        report[6] = 1;
        if (7 + body.Length > Length) throw new InvalidOperationException("조명 효과가 너무 길어 한 번에 보낼 수 없습니다.");
        body.CopyTo(report, 7);
        BitConverter.GetBytes((ushort)(3 + body.Length)).CopyTo(report, 2);
        return report;
    }

    public static byte[] Encode(IReadOnlyList<SpectrumEffect> effects)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < effects.Count; i++)
        {
            var e = effects[i];
            if (e.Colors.Length > 255 || e.Keys.Length > 255) throw new InvalidOperationException("한 효과에 색이나 키가 너무 많습니다.");
            bytes.AddRange([(byte)(i + 1), 6, 1, (byte)e.Kind, 2, e.Speed, 3, e.Clockwise, 4, e.Direction, 5, (byte)e.ColorMode, 6, 0]);
            bytes.Add((byte)e.Colors.Length);
            foreach (var c in e.Colors) bytes.AddRange([c.R, c.G, c.B]);
            bytes.Add((byte)e.Keys.Length);
            foreach (var k in e.Keys) bytes.AddRange(BitConverter.GetBytes(k));
        }
        return [.. bytes];
    }

    // An effect answer (CC): header, profile, 01 01, then effects numbered from 1 until a number
    // goes backwards or reaches the zero padding. Each effect header has fixed marker bytes.
    public static SpectrumEffect[] ParseEffects(byte[] answer)
    {
        var effects = new List<SpectrumEffect>();
        int i = 7, last = 0;
        while (i + 16 <= answer.Length)
        {
            int number = answer[i];
            if (number == 0 || number <= last) break;
            if (answer[i + 1] != 6 || answer[i + 2] != 1 || answer[i + 4] != 2 || answer[i + 6] != 3 || answer[i + 8] != 4 || answer[i + 10] != 5)
                throw new InvalidDataException($"조명 효과 {number}번을 읽지 못했습니다.");
            var kind = (LightingEffectKind)answer[i + 3];
            byte speed = answer[i + 5], rotation = answer[i + 7], direction = answer[i + 9];
            var mode = (LightingColorMode)answer[i + 11];
            i += 14;
            int colorCount = answer[i++];
            if (i + colorCount * 3 + 1 > answer.Length) throw new InvalidDataException("조명 효과의 색 목록이 잘렸습니다.");
            var colors = new LightingColor[colorCount];
            for (int c = 0; c < colorCount; c++, i += 3) colors[c] = new(answer[i], answer[i + 1], answer[i + 2]);
            int keyCount = answer[i++];
            if (i + keyCount * 2 > answer.Length) throw new InvalidDataException("조명 효과의 키 목록이 잘렸습니다.");
            var keys = new ushort[keyCount];
            for (int k = 0; k < keyCount; k++, i += 2) keys[k] = BitConverter.ToUInt16(answer, i);
            effects.Add(new(kind, speed, rotation, direction, mode, colors, keys));
            last = number;
        }
        return [.. effects];
    }

    // The live LED colors (opcode 03): a u16 byte count, then {u16 code, R, G, B} per light.
    public static Dictionary<ushort, LightingColor> ParseState(byte[] report)
    {
        var colors = new Dictionary<ushort, LightingColor>();
        if (report.Length < 4 || report[1] != State) return colors;
        int count = BitConverter.ToUInt16(report, 2) / 5;
        for (int i = 0, at = 4; i < count && at + 5 <= report.Length; i++, at += 5)
            colors[BitConverter.ToUInt16(report, at)] = new(report[at + 2], report[at + 3], report[at + 4]);
        return colors;
    }

    public static ushort[] ParseKeyPage(byte[] answer, int columns)
    {
        // [4] matrix (7 keys, 8 extra), [5] row, then {column, u16 code} per cell.
        var codes = new ushort[columns];
        for (int c = 0; c < columns; c++)
        {
            int at = 6 + c * 3;
            if (at + 3 > answer.Length) break;
            codes[c] = BitConverter.ToUInt16(answer, at + 1);
        }
        return codes;
    }
}

public interface ISpectrumTransport : IDisposable
{
    bool SetFeature(byte[] report);
    bool GetFeature(byte[] report);
}

// Request/answer handling on top of the raw report, including the lag described above.
public sealed class SpectrumClient(ISpectrumTransport transport, Func<long>? clock = null, Action<int>? sleep = null) : IDisposable
{
    private const int IdleWait = 400, AnswerWait = 300, Attempts = 3, EffectSettle = 600, ProfileSettle = 150;
    private readonly Func<long> clock = clock ?? (() => Environment.TickCount64);
    private readonly Action<int> sleep = sleep ?? Thread.Sleep;
    private long quietUntil;

    public void Dispose() => transport.Dispose();

    // The live colors, or null while the report still holds an answer.
    public Dictionary<ushort, LightingColor>? ReadColors()
    {
        var report = Read();
        return report[1] == SpectrumPacket.State ? SpectrumPacket.ParseState(report) : null;
    }
    public int ReadBrightness() => Ask(SpectrumPacket.GetBrightness)[4];
    public int ReadProfile() => Ask(SpectrumPacket.Profile)[4];
    public bool ReadLogo() => Ask(SpectrumPacket.GetLogo)[4] == 1;
    public SpectrumEffect[] ReadEffects(int profile) =>
        SpectrumPacket.ParseEffects(Ask(SpectrumPacket.Effect, [(byte)profile], answer => answer[4] == profile));
    public SpectrumLayout ReadLayout()
    {
        var size = Ask(SpectrumPacket.KeyCount, [7], a => a[4] == 7);
        int rows = Math.Min((int)size[5], 16), columns = Math.Min((int)size[6], 32);
        var matrix = new ushort[rows][];
        for (int r = 0; r < rows; r++)
        {
            byte row = (byte)r;
            matrix[r] = SpectrumPacket.ParseKeyPage(Ask(SpectrumPacket.KeyPage, [7, row], a => a[4] == 7 && a[5] == row), columns);
        }
        var extraSize = Ask(SpectrumPacket.KeyCount, [8], a => a[4] == 8);
        var extra = extraSize[5] > 0
            ? SpectrumPacket.ParseKeyPage(Ask(SpectrumPacket.KeyPage, [8, 0], a => a[4] == 8 && a[5] == 0), Math.Min((int)extraSize[6], 32))
            : [];
        return new(matrix, extra);
    }

    public void SetBrightness(int level) => Send(SpectrumPacket.Request(SpectrumPacket.Brightness, (byte)Math.Clamp(level, 0, SpectrumPacket.MaxBrightness)));
    public void SetLogo(bool on) => Send(SpectrumPacket.Request(SpectrumPacket.Logo, on ? (byte)1 : (byte)0));
    public void SetProfile(int profile)
    {
        if (profile is < 1 or > SpectrumPacket.Profiles) throw new ArgumentOutOfRangeException(nameof(profile));
        Send(SpectrumPacket.Request(SpectrumPacket.ProfileChange, (byte)profile));
        quietUntil = clock() + ProfileSettle;
    }
    public void WriteEffects(int profile, IReadOnlyList<SpectrumEffect> effects)
    {
        Send(SpectrumPacket.EffectRequest(profile, effects));
        quietUntil = clock() + EffectSettle;
    }
    public void ResetProfile(int profile)
    {
        if (profile is < 1 or > SpectrumPacket.Profiles) throw new ArgumentOutOfRangeException(nameof(profile));
        Send(SpectrumPacket.Request(SpectrumPacket.ProfileDefault, (byte)profile));
        quietUntil = clock() + EffectSettle;
    }

    internal byte[] Ask(byte op, byte[]? args = null, Func<byte[], bool>? match = null)
    {
        var request = SpectrumPacket.Request(op, args ?? []);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            WaitIdle();
            if (!transport.SetFeature(request)) throw new IOException($"조명 장치에 요청하지 못했습니다 ({Marshal.GetLastWin32Error()}).");
            long until = clock() + AnswerWait;
            do
            {
                var answer = Read();
                if (answer[1] == op && (match is null || match(answer))) return answer;
                sleep(2);
            }
            while (clock() < until);
        }
        throw new TimeoutException($"조명 장치가 요청 0x{op:X2}에 답하지 않습니다.");
    }
    private void Send(byte[] request)
    {
        WaitIdle();
        if (!transport.SetFeature(request)) throw new IOException($"조명 장치에 보내지 못했습니다 ({Marshal.GetLastWin32Error()}).");
    }
    // Until the report shows the live colors again: an answer still in it would be mistaken for the
    // next one. Another program's requests can keep it busy, so this gives up after a while.
    private void WaitIdle()
    {
        long wait = quietUntil - clock();
        if (wait > 0) sleep((int)wait);
        long until = clock() + IdleWait;
        while (Read()[1] != SpectrumPacket.State && clock() < until) sleep(10);
    }
    private byte[] Read()
    {
        var report = new byte[SpectrumPacket.Length];
        report[0] = SpectrumPacket.ReportId;
        if (!transport.GetFeature(report)) throw new IOException($"조명 장치를 읽지 못했습니다 ({Marshal.GetLastWin32Error()}).");
        return report;
    }
}

// The HID collection itself, found by its capabilities rather than its instance path.
public sealed class HidSpectrumTransport : ISpectrumTransport
{
    private const ushort Vendor = 0x048D, UsagePage = 0xFF89, Usage = 0x0007;
    private readonly SafeFileHandle handle;
    public string Path { get; }
    private HidSpectrumTransport(SafeFileHandle handle, string path) { this.handle = handle; Path = path; }
    public void Dispose() => handle.Dispose();
    public bool SetFeature(byte[] report) => HidD_SetFeature(handle, report, report.Length);
    public bool GetFeature(byte[] report) => HidD_GetFeature(handle, report, report.Length);

    public static HidSpectrumTransport? Open()
    {
        foreach (var path in Interfaces())
        {
            if (!path.Contains("vid_048d", StringComparison.OrdinalIgnoreCase)) continue;
            using (var probe = CreateFile(path, 0, 3, 0, 3, 0, 0))
            {
                if (probe.IsInvalid || !Matches(probe)) continue;
            }
            var handle = CreateFile(path, 0xC0000000, 3, 0, 3, 0, 0);
            if (!handle.IsInvalid) return new(handle, path);
            handle.Dispose();
        }
        return null;
    }
    private static bool Matches(SafeFileHandle handle)
    {
        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != Vendor || (attributes.ProductId & 0xFF00) != 0xC900) return false;
        if (!HidD_GetPreparsedData(handle, out var data)) return false;
        try
        {
            var caps = new HidpCaps();
            return HidP_GetCaps(data, ref caps) == 0x00110000 && caps.UsagePage == UsagePage && caps.Usage == Usage
                && caps.FeatureReportByteLength == SpectrumPacket.Length;
        }
        finally { HidD_FreePreparsedData(data); }
    }
    private static List<string> Interfaces()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out var guid);
        nint set = SetupDiGetClassDevs(ref guid, 0, 0, 0x12);
        if (set == -1) return paths;
        try
        {
            for (int index = 0; ; index++)
            {
                var item = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref item)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref item, 0, 0, out int required, 0);
                if (required <= 0) continue;
                nint buffer = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(buffer, 8);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref item, buffer, required, out _, 0))
                        paths.Add(Marshal.PtrToStringUni(buffer + 4) ?? "");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public int Size; public Guid ClassGuid; public int Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct HiddAttributes { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] private struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkCollectionNodes, InputButtonCaps, InputValueCaps, InputDataIndices, OutputButtonCaps, OutputValueCaps,
            OutputDataIndices, FeatureButtonCaps, FeatureValueCaps, FeatureDataIndices;
    }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(nint data, ref HidpCaps caps);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] report, int length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] report, int length);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetupDiGetClassDevs(ref Guid guid, nint enumerator, nint parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, ref Guid guid, int index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref InterfaceData data, nint detail, int size, out int required, nint device);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
