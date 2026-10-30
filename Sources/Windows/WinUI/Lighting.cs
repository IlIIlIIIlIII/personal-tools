using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalTools;

// One zone as the page shows it: the effect that covers it in the current profile.
public sealed record LightingZoneState(LightingZone Zone, LightingEffectKind? Kind, LightingColor? Color, bool Random, int Speed, int KeyCount);
// Revision counts the page commands done, so the page redraws after every one, even one that left
// the firmware as it was (a stale toggle would otherwise stay on screen, as on 2026-10-28).
public sealed record LightingSnapshot(bool Ready, bool Found, bool WindowsControls, int Brightness, int Profile, bool LogoOn,
    LightingZoneState[] Zones, string? Error = null, int Revision = 0)
{
    // The key matrix only changes the preview, which the page redraws on its own.
    [JsonIgnore] public SpectrumLayout? Layout { get; init; }
    internal string RenderKey => JsonSerializer.Serialize(this) + (Layout is null ? "" : " layout");
    public static readonly LightingSnapshot Empty = new(false, false, false, 0, 0, false, []);
}

internal static class LightingZones
{
    public static readonly LightingZone[] All = [LightingZone.Keyboard, LightingZone.Bar, LightingZone.Logo];

    public static SpectrumEffect? EffectFor(IEnumerable<SpectrumEffect> effects, LightingZone zone) => effects.FirstOrDefault(e => SpectrumCodes.Covers(e, zone));

    public static ushort[] Keys(SpectrumLayout? layout, IEnumerable<SpectrumEffect> effects, LightingZone zone)
    {
        var keys = layout?.Keys(zone) ?? [];
        return keys.Length > 0 ? keys
            : effects.SelectMany(e => e.Keys).Where(k => SpectrumCodes.ZoneOf(k) == zone).Distinct().OrderBy(k => k).ToArray();
    }

    public static LightingZoneState Describe(IReadOnlyList<SpectrumEffect> effects, LightingZone zone, int keyCount)
    {
        var effect = EffectFor(effects, zone);
        if (effect is null) return new(zone, null, null, false, 0, keyCount);
        var color = effect.ColorMode == LightingColorMode.List && effect.Colors.Length > 0 ? effect.Colors[0] : (LightingColor?)null;
        return new(zone, effect.Kind, color, effect.ColorMode == LightingColorMode.Random, effect.Speed, keyCount);
    }

    // The effect the page asks for. Settings the effect does not take are 0; direction and rotation,
    // which the page does not offer, keep what the zone had or the factory value.
    public static SpectrumEffect Build(LightingEffectKind kind, LightingColor? color, bool random, int speed, SpectrumEffect? previous, ushort[] keys)
    {
        byte rotation = LightingEffects.HasRotation(kind) ? previous?.Clockwise is 1 or 2 ? previous.Clockwise : (byte)1 : (byte)0;
        byte direction = LightingEffects.HasDirection(kind) ? previous?.Direction is >= 1 and <= 4 ? previous.Direction : (byte)3 : (byte)0;
        byte pace = LightingEffects.HasSpeed(kind) ? (byte)Math.Clamp(speed, 1, 3) : (byte)0;
        if (!LightingEffects.HasColor(kind)) return new(kind, pace, rotation, direction, LightingColorMode.None, [], keys);
        if (random) return new(kind, pace, rotation, direction, LightingColorMode.Random, [], keys);
        var pick = color ?? (previous?.ColorMode == LightingColorMode.List && previous.Colors.Length > 0 ? previous.Colors[0] : LightingColor.LegionBlue);
        return new(kind, pace, rotation, direction, LightingColorMode.List, [pick], keys);
    }

    // The profile after one zone changes: one effect per zone, each on that zone's keys only, in
    // keyboard, bar, logo order. Lenovo's audio and screen effects take every light and cannot be
    // split, so a zone that still has one becomes white when another zone is changed.
    public static SpectrumEffect[] Compose(IReadOnlyList<SpectrumEffect> current, LightingZone zone, SpectrumEffect replacement, Func<LightingZone, ushort[]> keys)
    {
        var result = new List<SpectrumEffect>();
        foreach (var each in All)
        {
            if (each == zone) { result.Add(replacement); continue; }
            var existing = EffectFor(current, each);
            var zoneKeys = keys(each);
            if (existing is null || zoneKeys.Length == 0) continue;
            if (LightingEffects.NeedsLenovoApp(existing.Kind))
                existing = new(LightingEffectKind.Static, 0, 0, 0, LightingColorMode.List, [LightingColor.White], zoneKeys);
            result.Add(existing with { Keys = zoneKeys });
        }
        return [.. result];
    }
}

// Windows Dynamic Lighting drives the same LEDs through the LampArray side (048D:C903) and hides
// every firmware effect while it does. Settings' "이 디바이스에서 동적 조명 사용" is the
// AmbientLightingEnabled value under the device's key, and Windows watches it: writing 0 hands the
// LEDs back to the firmware at once (measured 2026-10-30). Only this laptop's key is touched.
internal interface IDynamicLighting
{
    bool? Controls();
    void TurnOff();
}

internal sealed class WindowsDynamicLighting(string root = @"Software\Microsoft\Lighting") : IDynamicLighting
{
    private const string Value = "AmbientLightingEnabled";
    public bool? Controls()
    {
        if (Read(root) == 0) return false;
        var device = DeviceKey();
        if (device is null) return null;
        return Read(root + @"\Devices\" + device) != 0;
    }
    public void TurnOff()
    {
        var device = DeviceKey() ?? throw new InvalidOperationException("Windows 동적 조명 설정에서 이 노트북을 찾지 못했습니다.");
        using var key = Registry.CurrentUser.OpenSubKey(root + @"\Devices\" + device, true)
            ?? throw new InvalidOperationException("Windows 동적 조명 설정을 열지 못했습니다.");
        key.SetValue(Value, 0, RegistryValueKind.DWord);
    }
    // Windows names the key after the LampArray interface: HID#VID_048D&PID_C903&MI_01#...
    private string? DeviceKey()
    {
        using var devices = Registry.CurrentUser.OpenSubKey(root + @"\Devices");
        return devices?.GetSubKeyNames().FirstOrDefault(name =>
            name.Contains("VID_048D&PID_C9", StringComparison.OrdinalIgnoreCase) && name.Contains("&MI_01", StringComparison.OrdinalIgnoreCase));
    }
    // A missing value means Windows' default, which is on.
    private static int Read(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(Value) is int value ? value : 1;
    }
}

// log.txt under %LOCALAPPDATA%\PersonalTools\Lighting: every write and failure, in Korean.
internal sealed class LightingFiles(string? folder = null)
{
    private const long LogLimit = 128 * 1024;
    public string Folder { get; } = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalTools", "Lighting");
    public string LogPath => Path.Combine(Folder, "log.txt");
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
        catch { /* Logging never stops the lighting page. */ }
    }
}

// 조명 page backend (design canvas https://claude.ai/artifact/6EANCaKQMaEjWsb5MN7Gz7). The
// settings live in the laptop's firmware (brightness, profile 1..6 cycled by Fn+Space, one effect
// per zone in each profile, logo on/off), so nothing is saved here and the lights stay as they are
// when the app is closed. The device is only touched while the page is visible or when the page
// asks: brightness, profile and logo are read every second (Fn keys change them), the live colors
// four times a second for the preview.
internal sealed class LightingController : IDisposable
{
    private const int FrameTick = 250, PollTick = 1000, OpenRetry = 5000;
    private readonly Func<SpectrumClient?> open;
    private readonly IDynamicLighting windows;
    private readonly LightingFiles files;
    private readonly Func<long> clock;
    private readonly ManualResetEvent stop = new(false);
    private readonly AutoResetEvent signal = new(false);
    private readonly ConcurrentQueue<Action> commands = new();
    private Thread? thread;
    private int disposed;
    private volatile bool visible;
    private SpectrumClient? client;
    private SpectrumLayout? layout;
    private SpectrumEffect[] effects = [];
    private int effectsProfile, brightness, profile, revision;
    // Effects are read again on the next poll (page shown again, connection reopened).
    private bool effectsStale;
    private bool logo, found, ready, windowsControls;
    private string? error;
    private long nextPoll, nextFrame, nextOpen;
    private int pendingBrightness = -1;
    private LightingSnapshot snapshot = LightingSnapshot.Empty;
    private string? published;

    public LightingSnapshot Snapshot => Volatile.Read(ref snapshot);
    // Both raised on the controller thread.
    public event Action<LightingSnapshot>? Changed;
    public event Action<IReadOnlyDictionary<ushort, LightingColor>>? Colors;

    internal LightingController(Func<SpectrumClient?>? open = null, IDynamicLighting? windows = null,
        LightingFiles? files = null, Func<long>? clock = null)
    {
        this.open = open ?? (() => HidSpectrumTransport.Open() is { } transport ? new SpectrumClient(transport) : null);
        this.windows = windows ?? new WindowsDynamicLighting();
        this.files = files ?? new LightingFiles();
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    public void Start()
    {
        if (thread is not null || disposed != 0) return;
        thread = new Thread(Run) { IsBackground = true, Name = "Personal Tools lighting" };
        thread.Start();
    }
    // Queued writes are dropped; the device handle is closed on the controller thread when it ends.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Set();
        if (thread is null) { client?.Dispose(); client = null; }
        else if (thread != Thread.CurrentThread) thread.Join(2000);
    }

    // MainWindow calls these as the page is shown and hidden (another page, the tray, minimized).
    public void Shown()
    {
        visible = true;
        Post(() =>
        {
            nextPoll = 0;
            nextFrame = 0;
            effectsStale = true;
        });
    }
    public void Hidden() => visible = false;

    public void SetBrightness(int level)
    {
        // A slider drag sends many values; only the latest is written.
        Interlocked.Exchange(ref pendingBrightness, Math.Clamp(level, 0, SpectrumPacket.MaxBrightness));
        Post(() =>
        {
            int value = Interlocked.Exchange(ref pendingBrightness, -1);
            if (value < 0 || Client() is not { } device) return;
            device.SetBrightness(value);
            brightness = value;
            files.Log($"밝기 {value}");
        });
    }
    public void SetProfile(int value) => Post(() =>
    {
        if (Client() is not { } device || value == profile) return;
        device.SetProfile(value);
        profile = value;
        LoadEffects(device);
        files.Log($"프로필 {value}");
    });
    public void SetLogo(bool on) => Post(() =>
    {
        if (Client() is not { } device) return;
        device.SetLogo(on);
        logo = on;
        files.Log(on ? "로고 켬" : "로고 끔");
    });
    // The page sends only what the click changed; the rest comes from the zone as it is now, so two
    // quick clicks (an effect, then a color) both stay. A command for a profile that is no longer
    // the current one (Fn+Space meanwhile) is dropped.
    public void SetZone(LightingZone zone, int expectedProfile, LightingEffectKind? kind = null, LightingColor? color = null,
        bool? random = null, int? speed = null) => Post(() =>
    {
        if (Client() is not { } device || profile < 1) return;
        if (profile != expectedProfile) { files.Log($"프로필이 {expectedProfile}에서 {profile}(으)로 바뀌어 {Name(zone)} 변경을 건너뜀"); return; }
        if (effectsProfile != profile || effectsStale) LoadEffects(device);
        var keys = LightingZones.Keys(layout, effects, zone);
        if (keys.Length == 0) throw new InvalidOperationException("이 구역의 키 목록을 읽지 못했습니다.");
        var current = LightingZones.Describe(effects, zone, keys.Length);
        var nextKind = kind ?? current.Kind ?? LightingEffectKind.Static;
        bool nextRandom = random ?? (color is null && current.Random);
        var replacement = LightingZones.Build(nextKind, color ?? current.Color, nextRandom && LightingEffects.HasColor(nextKind),
            speed ?? (current.Speed > 0 ? current.Speed : 2), LightingZones.EffectFor(effects, zone), keys);
        var next = LightingZones.Compose(effects, zone, replacement, z => LightingZones.Keys(layout, effects, z));
        device.WriteEffects(profile, next);
        LoadEffects(device);
        files.Log($"프로필 {profile} {Name(zone)}: {LightingEffects.Name(nextKind)}" + (replacement.ColorMode == LightingColorMode.List ? " " + replacement.Colors[0].Hex
            : replacement.ColorMode == LightingColorMode.Random ? " 무작위" : "") + (replacement.Speed > 0 ? $" 속도 {replacement.Speed}" : ""));
    });
    // Only the profile the user confirmed is reset.
    public void ResetProfile(int expectedProfile) => Post(() =>
    {
        if (Client() is not { } device || profile < 1) return;
        if (profile != expectedProfile) { files.Log($"프로필이 {expectedProfile}에서 {profile}(으)로 바뀌어 공장 설정 되돌리기를 건너뜀"); return; }
        device.ResetProfile(profile);
        LoadEffects(device);
        files.Log($"프로필 {profile} 공장 설정으로");
    });
    public void TurnOffWindowsLighting() => Post(() =>
    {
        windows.TurnOff();
        windowsControls = windows.Controls() == true;
        files.Log("이 노트북에서 Windows 동적 조명 끔");
        nextFrame = 0;
    });

    private void Post(Action command) { commands.Enqueue(command); signal.Set(); }

    private void Run()
    {
        var handles = new WaitHandle[] { stop, signal };
        try
        {
            while (true)
            {
                int wait = Timeout.Infinite;
                if (visible) wait = (int)Math.Clamp(Math.Min(nextPoll, nextFrame) - clock(), 0, PollTick);
                if (WaitHandle.WaitAny(handles, wait) == 0) return;
                long now = clock();
                bool poll = visible && now >= nextPoll, frame = visible && now >= nextFrame;
                if (poll) nextPoll = now + PollTick;
                if (frame) nextFrame = now + FrameTick;
                // An exception here would end the whole app (clipboard, audio, HDR with it).
                try { Step(poll, frame); }
                catch (Exception e) { files.Log("오류: " + e); }
            }
        }
        finally
        {
            client?.Dispose();
            client = null;
        }
    }
    // One pass of the controller thread: queued page commands, then the reads that are due.
    internal void Step(bool poll, bool frame)
    {
        while (!stop.WaitOne(0) && commands.TryDequeue(out var command))
        {
            try { command(); error = null; }
            catch (Exception e) { Fail(e); }
            revision++;
            Publish();
        }
        if (poll)
        {
            try { Poll(); }
            catch (Exception e) { Fail(e); }
            Publish();
        }
        if (frame)
        {
            try { if (client?.ReadColors() is { Count: > 0 } colors) Colors?.Invoke(colors); }
            catch (Exception e) { Fail(e); Publish(); }
        }
    }

    private void Poll()
    {
        windowsControls = windows.Controls() == true;
        if (Client() is not { } device) { ready = true; return; }
        layout ??= device.ReadLayout();
        brightness = device.ReadBrightness();
        profile = device.ReadProfile();
        logo = device.ReadLogo();
        if (effectsProfile != profile || effectsStale) LoadEffects(device);
        ready = true;
        error = null;
    }
    private void LoadEffects(SpectrumClient device)
    {
        effects = device.ReadEffects(profile);
        effectsProfile = profile;
        effectsStale = false;
    }
    // Opens the device on first use and again after a failure, at most every few seconds.
    private SpectrumClient? Client()
    {
        if (client is not null) return client;
        if (clock() < nextOpen) return null;
        nextOpen = clock() + OpenRetry;
        client = open();
        found = client is not null;
        if (!found) files.Log("조명 장치(048D:C909 FF89:0007)를 찾지 못함");
        return client;
    }
    private void Fail(Exception e)
    {
        error = e is IOException or TimeoutException
            ? "조명 장치와 통신하지 못했습니다. 잠시 뒤 다시 시도합니다."
            : e.Message;
        files.Log("오류: " + e.Message);
        if (e is IOException or TimeoutException or InvalidDataException or ObjectDisposedException)
        {
            client?.Dispose();
            client = null;
            // The zones keep showing what was last read until the next poll reads them again.
            effectsStale = true;
            nextOpen = clock() + 1000;
        }
    }
    private void Publish()
    {
        try
        {
            var zones = effectsProfile == profile && profile > 0
                ? LightingZones.All.Select(z => LightingZones.Describe(effects, z, LightingZones.Keys(layout, effects, z).Length)).ToArray()
                : [];
            var value = new LightingSnapshot(ready, found, windowsControls, brightness, profile, logo, zones, error, revision) { Layout = layout };
            string key = value.RenderKey;
            Volatile.Write(ref snapshot, value);
            if (key == published) return;
            published = key;
            Changed?.Invoke(value);
        }
        catch (Exception e) { files.Log("화면 갱신 실패: " + e.Message); }
    }

    internal static string Name(LightingZone zone) => zone switch
    {
        LightingZone.Keyboard => "키보드",
        LightingZone.Bar => "앞쪽 라이트바",
        _ => "덮개 로고",
    };
}
