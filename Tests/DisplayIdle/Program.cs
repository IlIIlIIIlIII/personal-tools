using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PersonalTools;

// --probe reads every monitor's power mode. --live [seconds] really turns the MO32U24 pair off and on,
// so tell the user before running it.
if (args.Contains("--probe"))
{
    var ddc = new DisplayDdc();
    var all = ddc.List();
    foreach (var m in all)
        Console.WriteLine($"{m.Label} | {DisplayIdleController.Describe(m, all.FirstOrDefault(x => x.Primary))} | D6={(m.Internal ? "-" : ddc.Read(m.Id)?.ToString() ?? "응답 없음")} | {m.Id}");
    return;
}
if (args.Contains("--live"))
{
    int seconds = args.SkipWhile(a => a != "--live").Skip(1).Select(a => int.TryParse(a, out int s) ? s : 0).FirstOrDefault(s => s > 0);
    if (seconds == 0) seconds = 5;
    var ddc = new DisplayDdc();
    var targets = ddc.List().Where(new DisplayIdleSettings().Selects).ToArray();
    string Layout() => string.Join(" ", new HdrDisplay().Read().Select(m => m.Name).OrderBy(n => n));
    string before = Layout();
    Console.WriteLine($"targets: {string.Join(", ", targets.Select(t => t.Label))}, layout: {before}");
    var clock = Stopwatch.StartNew();
    Parallel.ForEach(targets, t => Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms off {t.Label}: {ddc.Set(t.Id, false)}"));
    Thread.Sleep(2000);
    foreach (var t in targets) Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms {t.Label} D6={ddc.Read(t.Id)}");
    Thread.Sleep(Math.Max(0, seconds * 1000 - 2000));
    Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms layout while off unchanged: {Layout() == before}");
    var wake = Stopwatch.StartNew();
    Parallel.ForEach(targets, t =>
    {
        ddc.Set(t.Id, true);
        while (ddc.Read(t.Id) != 1 && wake.ElapsedMilliseconds < 8000) Thread.Sleep(250);
        Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms {t.Label} on after {wake.ElapsedMilliseconds} ms, D6={ddc.Read(t.Id)}");
    });
    Console.WriteLine($"layout after unchanged: {Layout() == before}");
    return;
}

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action run) { run(); Console.WriteLine("PASS " + name); passed++; }
// This PC's layout on 2026-10-23.
const string Guid = "#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
var lower = new DisplayInfo(@"\\?\DISPLAY#GBT3219#5&2eb3797f&0&UID8448" + Guid, "MO32U24", "MO32U24 · 2", 5, true, 0, 0, 2560, 1440);
var upper = new DisplayInfo(@"\\?\DISPLAY#GBT3219#5&2eb3797f&0&UID8452" + Guid, "MO32U24", "MO32U24 · 3", 10, false, 0, -1440, 2560, 1440);
var side = new DisplayInfo(@"\\?\DISPLAY#COS3150#5&2eb3797f&0&UID8454" + Guid, "32RTX950", "32RTX950", 10, false, -1440, -1961, 1440, 2560);
var laptop = new DisplayInfo(@"\\?\DISPLAY#BOE0D5B#4&36b9ac&0&UID8388688" + Guid, "NE18NZ2", "내장 디스플레이", 0x80000000, false, 2560, -1440, 1920, 1200);
// The on switch of each test lives under this key; removed when the tests end.
const string TestKeys = @"Software\PersonalTools\DisplayIdleTests";
(DisplayIdleController, Displays, Input, DisplayIdleFiles) Make(Action<Displays>? setup = null, DisplayIdleSettings? settings = null, bool on = true)
{
    string id = System.Guid.NewGuid().ToString("N");
    var files = new DisplayIdleFiles(Path.Combine(Path.GetTempPath(), "PersonalToolsDisplayIdleTests", id), $@"{TestKeys}\{id}");
    files.SaveSettings((settings ?? new()) with { Enabled = on });
    var displays = new Displays([upper, lower, side, laptop]);
    setup?.Invoke(displays);
    var input = new Input();
    return (new DisplayIdleController(displays, input, files, _ => { }), displays, input, files);
}
const long Minute = 60_000;

Test("native layout", () => Check(Marshal.SizeOf<IdleInput.XInputState>() == 16, "XINPUT_STATE size"));
Test("darkens the MO32U24 pair only after a minute without input", () =>
{
    var (c, d, i, f) = Make();
    i.Last = 1_000;
    c.Tick(1_000 + Minute - 1); Check(!c.Dark && d.Writes.Count == 0, "too early");
    c.Tick(1_000 + Minute);
    Check(c.Dark && d.Power[upper.Id] == 4 && d.Power[lower.Id] == 4, "pair not off");
    Check(d.Power[side.Id] == 1 && !d.Writes.Any(w => w.StartsWith(side.Id) || w.StartsWith(laptop.Id)), "other monitor touched");
    Check(File.Exists(Path.Combine(f.Folder, "dark.json")), "no dark marker");
});
Test("waits for new input before waking", () =>
{
    var (c, d, i, _) = Make();
    c.Tick(Minute); int writes = d.Writes.Count;
    c.Tick(Minute + 30_000); c.Tick(Minute + 90_000);
    Check(c.Dark && d.Writes.Count == writes, "woke or re-darkened without input");
});
Test("input wakes only the panels it turned off", () =>
{
    var (c, d, i, f) = Make(x => { x.Power[lower.Id] = 4; });
    c.Tick(Minute);
    Check(d.Power[upper.Id] == 4 && !d.Writes.Contains(lower.Id + ":5"), "skipped panel touched");
    i.Last = Minute + 500; c.Tick(Minute + 600);
    Check(!c.Dark && d.Power[upper.Id] == 1 && d.Power[lower.Id] == 4, "hand-switched panel turned on");
    Check(!File.Exists(Path.Combine(f.Folder, "dark.json")), "marker left");
});
Test("silent panel is skipped, reported and nothing crashes", () =>
{
    var (c, d, i, _) = Make(x => { x.Power[upper.Id] = null; });
    c.Tick(Minute); Check(d.Power[lower.Id] == 4 && !d.Writes.Contains(upper.Id + ":5"), "silent panel");
    Check(c.Snapshot.Monitors.Single(m => m.Id == upper.Id).Responding == false, "not reported as silent");
    Check(c.Snapshot.Recent[0].Text == "입력 없음 1분 → 1대 끔 · MO32U24 · 3 응답 없음", "event: " + c.Snapshot.Recent[0].Text);
    i.Last = Minute + 1; c.Tick(Minute + 2); Check(d.Power[lower.Id] == 1, "not woken");
});
Test("input during the off commands still wakes", () =>
{
    var (c, d, i, _) = Make();
    c.Tick(Minute); i.Last = Minute + 1; c.Tick(Minute + 100);
    Check(!c.Dark && d.Power[upper.Id] == 1, "missed input");
});
Test("panel that does not come back gets the command again", () =>
{
    var (c, d, i, f) = Make();
    c.Tick(Minute); d.IgnoreOn[upper.Id] = 1;
    i.Last = Minute + 1; c.Tick(Minute + 2);
    Check(d.Power[upper.Id] == 1 && d.Writes.Count(w => w == upper.Id + ":1") == 2, "no retry");
    Check(File.ReadAllText(f.LogPath).Contains("2회"), "retry not logged");
});
Test("panel that ignored the off command gets it once more", () =>
{
    var (c, d, i, _) = Make(x => x.IgnoreOff[upper.Id] = 1);
    c.Tick(Minute); Check(d.Power[upper.Id] == 1, "fake ignored nothing");
    c.Tick(Minute + 2_000); Check(d.Power[upper.Id] == 4 && d.Writes.Count(w => w == upper.Id + ":5") == 2, "no second off");
    c.Tick(Minute + 4_000); Check(d.Writes.Count(w => w == upper.Id + ":5") == 2, "verified twice");
});
Test("settings file: longer timeout, other monitors, but not the on switch", () =>
{
    var (c, d, i, f) = Make(on: false);
    c.Tick(10 * Minute); Check(!c.Dark && d.Writes.Count == 0, "disabled still darkened");
    Check(!File.ReadAllText(f.SettingsPath).Contains("Enabled"), "on switch written to the file");
    File.WriteAllText(f.SettingsPath, """{"Enabled": true, "IdleSeconds": 120, "Monitors": ["32RTX950"]}""");
    File.SetLastWriteTimeUtc(f.SettingsPath, DateTime.UtcNow.AddSeconds(5));
    c.Tick(10 * Minute + 6_000);
    Check(!c.Dark && c.Snapshot.Settings is { Enabled: false, IdleSeconds: 120 }, "file not read or its Enabled used");
    c.SetEnabled(true); i.Last = 20 * Minute;
    c.Tick(20 * Minute + Minute); Check(!c.Dark, "used the old timeout");
    c.Tick(22 * Minute); Check(c.Dark && d.Power[side.Id] == 4 && d.Power[upper.Id] == 1, "wrong monitors");
});
Test("turning the feature off while dark wakes the panels", () =>
{
    var (c, d, i, f) = Make();
    c.Tick(Minute); Check(c.Dark, "not dark");
    c.SetEnabled(false); c.Tick(Minute + 100);
    Check(!c.Dark && d.Power[upper.Id] == 1 && d.Power[lower.Id] == 1, "still dark");
    Check(c.Snapshot.Recent[0].Text.StartsWith("기능 꺼짐 → "), "wake reason");
    Check(!f.LoadSettings().Enabled, "not saved");
});
Test("an app restart in the same sign-in keeps the feature on", () =>
{
    var (_, d, i, f) = Make();
    var restarted = new DisplayIdleController(d, i, f, _ => { });
    restarted.Tick(Minute); Check(restarted.Dark, "turned off by the restart");
});
Test("restarting Windows starts with the feature off: the on switch is a volatile key", () =>
{
    var (_, d, i, f) = Make(settings: new(IdleSeconds: 300));
    using (var key = Registry.CurrentUser.OpenSubKey(f.OnKey, true)!)
    {
        // Windows refuses a normal key under a volatile one.
        bool isVolatile = false;
        try { key.CreateSubKey("child").Dispose(); } catch (IOException) { isVolatile = true; }
        Check(isVolatile, "on switch would survive a restart");
    }
    Registry.CurrentUser.DeleteSubKey(f.OnKey);
    var booted = new DisplayIdleController(d, i, f, _ => { });
    booted.Tick(10 * Minute);
    Check(!booted.Dark && booted.Snapshot.Settings is { Enabled: false, IdleSeconds: 300 }, "not off, or the wait time was lost");
    booted.SetEnabled(true); booted.Tick(10 * Minute + 1); Check(booted.Dark, "turning it on by hand");
});
Test("timeout is never shorter than 10 seconds", () =>
{
    var (c, _, i, _) = Make(settings: new(IdleSeconds: 1));
    c.Tick(9_999); Check(!c.Dark, "below 10 s"); c.Tick(10_000); Check(c.Dark, "10 s");
});
Test("page: wait time is saved and used", () =>
{
    var (c, _, i, f) = Make();
    c.SetIdleSeconds(300); c.Tick(1);
    Check(c.Snapshot.Settings.IdleSeconds == 300 && File.ReadAllText(f.SettingsPath).Contains("300"), "not saved");
    Check(!File.ReadAllText(f.SettingsPath).Contains("Selectors"), "computed properties written");
    c.Tick(5 * Minute - 1); Check(!c.Dark, "old timeout"); c.Tick(5 * Minute); Check(c.Dark, "new timeout");
});
Test("page: one MO32U24 switched off keeps the other by device path", () =>
{
    var (c, d, i, _) = Make();
    c.SetMonitor(upper.Id, false); c.Tick(1);
    Check(c.Snapshot.Settings.Monitors!.SequenceEqual([lower.Id]), "selection: " + string.Join(", ", c.Snapshot.Settings.Monitors!));
    c.SetMonitor(side.Id, true); c.SetMonitor(laptop.Id, true); c.Tick(2);
    Check(c.Snapshot.Settings.Monitors!.Order().SequenceEqual(new[] { lower.Id, side.Id }.Order()), "laptop selected or side missing");
    c.Tick(Minute + 2);
    Check(d.Power[upper.Id] == 1 && d.Power[lower.Id] == 4 && d.Power[side.Id] == 4, "wrong panels dark");
});
Test("page: selection for a monitor that is not attached is kept", () =>
{
    var (c, d, _, _) = Make(settings: new(Monitors: [@"\\?\DISPLAY#OTHER#1", "MO32U24"]));
    c.Tick(0); c.SetMonitor(side.Id, true); c.Tick(1);
    Check(c.Snapshot.Settings.Monitors!.Contains(@"\\?\DISPLAY#OTHER#1") && c.Snapshot.Settings.Monitors!.Length == 4, "lost the unplugged monitor");
});
Test("page: 지금 끄기 ignores the hand still on the mouse", () =>
{
    var (c, d, i, _) = Make();
    i.Last = 10_000; c.DarkenNow(); c.Tick(10_000);
    Check(c.Dark && d.Power[upper.Id] == 4, "not dark");
    i.Last = 11_000; c.Tick(11_100); Check(c.Dark, "woke inside the grace");
    i.Last = 11_600; c.Tick(11_700); Check(!c.Dark && d.Power[upper.Id] == 1, "did not wake after the grace");
    Check(c.Snapshot.Recent[1].Text == "지금 끄기 → 2대 끔", "event: " + c.Snapshot.Recent[1].Text);
});
Test("page: 지금 끄기 does nothing while the feature is off", () =>
{
    var (c, d, _, _) = Make(on: false);
    c.Tick(0); c.DarkenNow(); c.Tick(1); Check(!c.Dark && d.Writes.Count == 0, "darkened while off");
});
Test("page: snapshot lists monitors, states and recent events", () =>
{
    var (c, d, i, _) = Make(x => x.Power[side.Id] = null);
    c.Refresh(); c.Tick(0);
    var monitors = c.Snapshot.Monitors;
    Check(monitors.Select(m => m.Name).SequenceEqual(["MO32U24 · 2", "MO32U24 · 3", "32RTX950", "내장 디스플레이"]), "order: " + string.Join(", ", monitors.Select(m => m.Name)));
    Check(monitors[0].Detail == "주 모니터 · HDMI" && monitors[1].Detail == "주 모니터 위 · DisplayPort" && monitors[2].Detail == "주 모니터 왼쪽 · DisplayPort", "details");
    Check(monitors[2].Responding == false && monitors[0].Responding == true && monitors[3] is { Supported: false, Responding: null, Selected: false }, "probe");
    Check(monitors[0].Selected && monitors[1].Selected && !monitors[2].Selected, "default selection");
    c.Tick(Minute);
    Check(c.Snapshot is { Dark: true, DarkCount: 2, DarkSince: not null } && c.Snapshot.Monitors.Count(m => m.Dark) == 2, "dark state");
    i.Last = Minute + 1; c.Tick(Minute + 2);
    var recent = c.Snapshot.Recent;
    Check(recent.Length == 2 && recent[0].Text == "입력 → MO32U24 · 2, MO32U24 · 3 켬" && recent[0].Note == "0.0초" && recent[1].Text == "입력 없음 1분 → 2대 끔", "events");
    Check(!c.Snapshot.Dark && c.Snapshot.DarkSince is null, "still dark");
    for (int n = 0; n < 2; n++) { long t = i.Last + Minute; c.Tick(t); Check(c.Dark, "cycle not dark"); i.Last = t + 1; c.Tick(t + 2); }
    Check(c.Snapshot.Recent.Length == 3, "recent not capped");
});
Test("restart wakes panels a previous run left dark", () =>
{
    var (c, d, _, f) = Make(x => x.Power[upper.Id] = 4);
    f.SaveDark([new(upper.Id, upper.Label)]);
    c.Recover();
    Check(!c.Dark && d.Power[upper.Id] == 1 && !File.Exists(Path.Combine(f.Folder, "dark.json")), "not recovered");
    Check(c.Snapshot.Recent[0].Text == "앱 시작 → MO32U24 · 3 켬", "event: " + c.Snapshot.Recent[0].Text);
});
Test("text helpers", () =>
{
    Check(DisplayIdleController.Duration(30) == "30초" && DisplayIdleController.Duration(60) == "1분" && DisplayIdleController.Duration(90) == "1분 30초", "duration");
    Check(DisplayIdleController.Subject("32RTX950") == "이" && DisplayIdleController.Subject("MO32U24") == "가" && DisplayIdleController.Subject("MO32U24 · 3") == "이", "digits");
    Check(DisplayIdleController.Subject("내장 디스플레이") == "가" && DisplayIdleController.Subject("모니터 왼쪽") == "이" && DisplayIdleController.Subject("DELL") == "이", "letters");
});
Test("gamepad: dead zone noise is not input, buttons and sticks are", () =>
{
    IdleInput.XInputState State(uint packet, ushort buttons = 0, short lx = 0, byte trigger = 0) =>
        new() { Packet = packet, Pad = new() { Buttons = buttons, ThumbLX = lx, LeftTrigger = trigger } };
    Check(!IdleInput.IsActive(State(2, lx: 3000), State(1, lx: 2500)), "noise counted");
    Check(!IdleInput.IsActive(State(1, buttons: 1), State(1)), "same packet counted");
    Check(IdleInput.IsActive(State(2, buttons: 0x1000), State(1)), "button press");
    Check(IdleInput.IsActive(State(3), State(2, buttons: 0x1000)), "button release");
    Check(IdleInput.IsActive(State(2, lx: 20000), State(1)), "stick");
    Check(IdleInput.IsActive(State(2, trigger: 200), State(1)), "trigger");
});
Test("real input clock and monitor list are sane", () =>
{
    long now = Environment.TickCount64, last = new IdleInput().LastInput(now);
    Check(last <= now && now - last < 49L * 24 * 3600 * 1000, $"last input {now - last} ms ago");
    var list = new DisplayDdc().List();
    Check(list.Count(m => m.Primary) == 1 && list.All(m => m.Width > 0 && m.Label.Length > 0), "monitor list");
});
Registry.CurrentUser.DeleteSubKeyTree(TestKeys, false);
Console.WriteLine($"{passed} passed");

sealed class Displays(DisplayInfo[] monitors) : IDisplayPowerControl
{
    public readonly Dictionary<string, uint?> Power = monitors.Where(m => !m.Internal).ToDictionary(m => m.Id, _ => (uint?)1);
    public readonly Dictionary<string, int> IgnoreOn = [], IgnoreOff = [];
    public readonly List<string> Writes = [];
    public DisplayInfo[] List() => monitors;
    public uint? Read(string id) { lock (this) return Power.GetValueOrDefault(id); }
    public bool Set(string id, bool on)
    {
        lock (this)
        {
            Writes.Add($"{id}:{(on ? 1 : 5)}");
            if (Power.GetValueOrDefault(id) is null) return false;
            var ignore = on ? IgnoreOn : IgnoreOff;
            if (ignore.TryGetValue(id, out int left) && left > 0) { ignore[id] = left - 1; return true; }
            Power[id] = on ? 1u : 4u;
            return true;
        }
    }
}
sealed class Input : IIdleInput
{
    public long Last;
    public long LastInput(long now) => Last;
}
