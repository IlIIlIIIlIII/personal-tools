using System.Runtime.InteropServices;
using System.Text.Json;
using PersonalTools;
using PersonalTools.HdrWidget;
using System.Diagnostics;
using System.IO.Pipes;

if (args.Contains("--fixture")) { Thread.Sleep(2500); return; }
if (args.Contains("--display-power-fixture")) { DisplayPowerChecks.HoldDisplay(); return; }
if (args.Contains("--live-power")) { await DisplayPowerChecks.CheckInstalledAsync(); return; }
if (args.Contains("--live-app")) { await LiveChecks.RunAsync(); return; }

if (args.Contains("--probe"))
{
    Console.WriteLine(JsonSerializer.Serialize(new HdrDisplay().Read(), new JsonSerializerOptions { WriteIndented = true }));
    return;
}

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action run) { run(); Console.WriteLine("PASS " + name); passed++; }
var game = new HdrProgram(Guid.NewGuid(), "Game", @"C:\Games\game.exe");
var process = new HdrProcess("10:100", game.Path);
var otherProcess = new HdrProcess("11:200", game.Path);

Test("native ABI layout", () =>
{
    Check(Marshal.SizeOf<HdrDisplay.PathInfo>() == 72, "path size");
    Check(Marshal.SizeOf<HdrDisplay.ModeInfo>() == 64, "mode size");
    Check(Marshal.SizeOf<HdrDisplay.TargetName>() == 420, "name size");
    Check(Marshal.SizeOf<HdrDisplay.Color2>() == 36 && Marshal.SizeOf<HdrDisplay.SetColor>() == 24, "packet size");
});
Test("full path match, case insensitive and polling reconciliation", () =>
{
    var d = new Displays(); var a = new HdrAutomation(d, new Journal());
    a.Reconcile([process with { Path = @"C:\Other\game.exe" }], [game]); Check(!d.On, "same basename must not match");
    a.Reconcile([process with { Path = game.Path.ToUpperInvariant() }], [game]); Check(d.On, "detected existing game");
    a.Reconcile([], [game]); Check(!d.On, "missed stop event recovered by reconciliation");
});
Test("overlapping games preserve initial snapshot", () =>
{
    var d = new Displays(); var a = new HdrAutomation(d, new Journal());
    a.Reconcile([process], [game]); a.Reconcile([process, otherProcess], [game]);
    a.Reconcile([otherProcess], [game]); Check(d.On, "early restore");
    a.Reconcile([], [game]); Check(!d.On && d.Writes == 2, "last game restore");
});
Test("already enabled HDR is preserved", () =>
{
    var d = new Displays { On = true }; var a = new HdrAutomation(d, new Journal());
    a.Reconcile([process], [game]); a.Reconcile([], [game]); Check(d.On && d.Writes == 0, "unnecessary mutation");
});
Test("game restarts between polls with new process identity", () =>
{
    var d = new Displays(); var a = new HdrAutomation(d, new Journal());
    a.Reconcile([process], [game]); a.Reconcile([otherProcess], [game]);
    Check(d.On, "replacement process didn't enable HDR after restoration");
    a.Reconcile([], [game]); Check(!d.On, "replacement process captured stale original state");
});
Test("manual off and on persist through overlapping games and restart", () =>
{
    var d = new Displays(); var j = new Journal(); var a = new HdrAutomation(d, j);
    a.Reconcile([process], [game]); a.SetManual("monitor-a", false);
    a.Reconcile([process, otherProcess], [game]); Check(!d.On, "automation overrode manual off");
    a.SetManual("monitor-a", true);
    a = new HdrAutomation(d, j); a.Reconcile([otherProcess], [game]); a.Reconcile([], [game]);
    Check(d.On, "manual on was restored away");
});
Test("external changes are preserved", () =>
{
    var d = new Displays(); var a = new HdrAutomation(d, new Journal());
    a.Reconcile([process], [game]); d.On = false; a.Reconcile([process], [game]);
    d.On = true; a.Reconcile([], [game]); Check(d.On, "external override lost");
});
Test("apply failure and partial apply retain recovery intent", () =>
{
    var d = new Displays { ThrowAfterWrite = true }; var j = new Journal(); var a = new HdrAutomation(d, j);
    a.Reconcile([process], [game]); Check(a.Error is not null && j.Load().Count == 1, "missing failure/journal");
    d.ThrowAfterWrite = false; a = new HdrAutomation(d, j); a.Reconcile([], [game]); Check(!d.On, "partial apply not restored");
});
Test("write-ahead journal failure prevents native mutation", () =>
{
    var d = new Displays(); var a = new HdrAutomation(d, new Journal { Fail = true });
    try { a.Reconcile([process], [game]); } catch (IOException) { }
    Check(d.Writes == 0, "changed without durable recovery");
});
Test("disconnect waits and never restores to replacement monitor", () =>
{
    var d = new Displays(); var j = new Journal(); var a = new HdrAutomation(d, j);
    a.Reconcile([process], [game]); d.Id = "monitor-b"; a.Reconcile([], [game]);
    Check(d.On && j.Load().Count == 1, "restored wrong monitor");
    d.Id = "monitor-a"; a.Reconcile([], [game]); Check(!d.On && j.Load().Count == 0, "pending restoration lost");
});
Test("crash restart adopts live session then restores", () =>
{
    var d = new Displays(); var j = new Journal(); var a = new HdrAutomation(d, j);
    a.Reconcile([process], [game]); a = new HdrAutomation(d, j); a.Reconcile([process], [game]);
    Check(d.On && d.Writes == 1, "recovery changed original");
    a.Reconcile([], [game]); Check(!d.On, "recovery did not restore");
});
Test("disable, PID reuse, normal shutdown", () =>
{
    var d = new Displays(); var a = new HdrAutomation(d, new Journal());
    a.Reconcile([process], [game]); a.Reconcile([process], [game with { Enabled = false }]); Check(!d.On, "disable didn't release");
    a.Reconcile([process with { Key = "10:300" }], [game]); Check(d.On, "PID reuse not detected");
    a.Shutdown(); Check(!d.On, "normal shutdown didn't restore");
});
Test("unsupported display remains unchanged", () =>
{
    var d = new Displays { Supported = false }; var a = new HdrAutomation(d, new Journal());
    a.Reconcile([process], [game]); Check(!d.On && a.Error is not null, "unsupported display mutation");
});
Test("primary changes apply only to subsequently started games", () =>
{
    var d = new MultipleDisplays(); var a = new HdrAutomation(d, new Journal());
    a.Reconcile([process], [game]); Check(d.First && !d.Second, "initial primary wrong");
    d.SecondPrimary = true; a.Reconcile([process, otherProcess], [game]);
    Check(d.First && d.Second, "new game did not use new primary");
    a.SetManual("second", true); a.Reconcile([otherProcess], [game]);
    Check(!d.First && d.Second, "independent restoration failed");
    a.Reconcile([], [game]); Check(d.Second, "manual state on other monitor lost");
});
Test("failed restore remains durable and retries", () =>
{
    var d = new Displays(); var j = new Journal(); var a = new HdrAutomation(d, j);
    a.Reconcile([process], [game]); d.ThrowBeforeWrite = true;
    a.Reconcile([], [game]); Check(d.On && j.Load().Count == 1 && a.Error is not null, "failed restore discarded");
    d.ThrowBeforeWrite = false; a.Reconcile([], [game]);
    Check(!d.On && j.Load().Count == 0, "restoration not retried");
});
Test("legacy EDID requires PQ HDR metadata", () =>
{
    byte[] edid = new byte[256]; edid[126] = 1; edid[128] = 2; edid[130] = 8;
    edid[132] = 0xe3; edid[133] = 6; edid[134] = 4;
    Check(HdrDisplay.HasPqMetadata(edid), "PQ not found"); edid[134] = 1;
    Check(!HdrDisplay.HasPqMetadata(edid), "SDR accepted as HDR");
    Check(!HdrDisplay.HasPqMetadata(new byte[12]), "truncated accepted");
});
Test("widget pagination, unsupported state, escaped names", () =>
{
    var monitors = Enumerable.Range(0, 5).Select(i => new HdrMonitor(i.ToString(), "Screen \"" + i, i == 0, i != 4, false)).ToArray();
    using var json = JsonDocument.Parse(WidgetCard.Create(new(monitors, [], null), 1, 2));
    Check(json.RootElement.GetProperty("body").EnumerateArray().Count(r => r.GetProperty("type").GetString() == "Container") == 2, "each monitor must occupy exactly one row");
    string card = WidgetCard.Create(new(monitors, [], null), 4, 1);
    Check(!card.Contains("monitorId"), "unsupported action rendered");
});
Test("large widget shows four monitors without pagination and stretches rows", () =>
{
    var monitors = Enumerable.Range(0, 4).Select(i => new HdrMonitor(i.ToString(), "Monitor " + i, i == 0, true, i == 0)).ToArray();
    using var card = JsonDocument.Parse(WidgetCard.Create(new(monitors, [], null), 1, WidgetCard.Capacity("large")));
    var rows = card.RootElement.GetProperty("body");
    Check(rows.GetArrayLength() == 4, "four monitor rows expected");
    Check(card.RootElement.GetProperty("actions").GetArrayLength() == 1, "only refresh expected without pagination");
    foreach (var row in rows.EnumerateArray().Where(r => r.GetProperty("type").GetString() == "Container"))
    {
        Check(row.GetProperty("height").GetString() == "stretch", "fixed widget height left unused");
        Check(row.GetProperty("items").GetArrayLength() == 1, "monitor split into multiple rows");
        var columns = row.GetProperty("items")[0].GetProperty("columns");
        Check(columns.GetArrayLength() == 3, "name, state and toggle must be inline");
        var toggle = columns[2].GetProperty("items")[0];
        var action = toggle.GetProperty("selectAction");
        string id = action.GetProperty("data").GetProperty("monitorId").GetString()!;
        Check(action.GetProperty("data").GetProperty("enabled").GetBoolean() == !monitors.Single(m => m.Id == id).Enabled, "toggle must request inverse of confirmed state");
        Check(toggle.GetProperty("url").GetString()!.StartsWith("data:image/png;base64,"), "toggle graphic must be self-contained");
        Check(action.GetProperty("verb").GetString() == "set", "toggle must apply immediately");
    }
    Check(WidgetCard.Capacity("medium") == 4 && WidgetCard.Capacity("small") == 2, "compact layout capacity");
});
Test("wire rejects oversized messages", () =>
{
    using var input = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
    try { HdrWire.ReadAsync<HdrRequest>(input, default).GetAwaiter().GetResult(); throw new Exception("accepted oversized input"); }
    catch (IOException) { }
});
Test("display requests exclude sleep-only requests and preserve reasons", () =>
{
    var entries = DisplayPowerParser.Parse("DISPLAY:\r\n[PROCESS] C:\\Video\\player.exe\r\nPlaying video\r\nsecond line\r\n[DRIVER] Display Driver\r\nDriver reason\r\n\r\nSYSTEM:\r\n[PROCESS] C:\\Audio.exe\r\nAudio stream\r\n");
    Check(entries.Length == 2 && entries[0].Kind == "PROCESS" && entries[0].Name.EndsWith("player.exe"), "wrong display entries");
    Check(entries[0].Reason == "Playing video\nsecond line" && entries[1].Reason == "Driver reason", "lost reasons or included SYSTEM");
    Check(DisplayPowerParser.Parse("DISPLAY:\n[PROCESS] player.exe\nReason:\nvideo\nSYSTEM:\n")[0].Reason == "Reason:\nvideo", "reason heading mistaken for section");
    Check(DisplayPowerParser.Parse("DISPLAY:\nNone.\nSYSTEM:\n[PROCESS] audio.exe\n").Length == 0, "sleep-only counted");
    Check(DisplayPowerParser.Parse("디스플레이:\n없음.\n시스템:\n없음.\n").Length == 0, "Korean empty result");
    Check(DisplayPowerParser.Parse("DISPLAY:\r\n없습니다.\r\n\r\nSYSTEM:\r\n없습니다.\r\n").Length == 0, "Windows Korean empty result");
});
Test("unknown, denied, and truncated output never means no requests", () =>
{
    foreach (string output in new[] { "", "This command requires administrator privileges", "DISPLAY:\n", "DISPLAY:\nNone.\n", "DISPLAY:\nunknown\nSYSTEM:\n", "DISPLAY:\n[PROCESS]\nSYSTEM:\n" })
    {
        bool rejected = false;
        try { DisplayPowerParser.Parse(output); } catch (IOException) { rejected = true; }
        Check(rejected, "invalid query accepted: " + output);
    }
});
Test("power widget states, pagination, escaping, and wire round trip", () =>
{
    var requests = Enumerable.Range(0, 5).Select(i => new DisplayPowerRequest("PROCESS", $"player\"{i}.exe", "동영상 재생")).ToArray();
    var snapshot = new HdrSnapshot([], [], null, DisplayPower: new(requests, DateTimeOffset.Now, Running: true));
    using var card = JsonDocument.Parse(WidgetCard.CreatePower(snapshot, 1, 1));
    Check(card.RootElement.GetProperty("body")[1].GetProperty("text").GetString()!.Contains("player\"1.exe"), "wrong page/escaped name");
    Check(card.RootElement.GetProperty("actions").GetArrayLength() == 2, "refresh and next page expected");
    using var empty = JsonDocument.Parse(WidgetCard.CreatePower(snapshot with { DisplayPower = new([], DateTimeOffset.Now) }, 0, 1));
    Check(empty.RootElement.GetProperty("body")[0].GetProperty("text").GetString()!.Contains("요청 없음"), "empty state");
    using var error = JsonDocument.Parse(WidgetCard.CreatePower(snapshot with { Error = "연결 실패" }, 0, 1));
    Check(error.RootElement.GetProperty("body")[0].GetProperty("text").GetString()!.Contains("확인 실패"), "connection failure shown as healthy");
    Check(!error.RootElement.ToString().Contains("player"), "stale names shown after failure");
    Check(new DisplayPowerSnapshot([]).Summary.Contains("시작 필요"), "permission needed mistaken as empty");
    using var wire = new MemoryStream();
    HdrWire.WriteAsync(wire, snapshot, default).GetAwaiter().GetResult(); wire.Position = 0;
    var copy = HdrWire.ReadAsync<HdrSnapshot>(wire, default).GetAwaiter().GetResult();
    Check(copy.DisplayPower!.Requests.SequenceEqual(requests), "IPC lost requests");
});
Test("HDR and display power widgets are independent definitions", () =>
{
    Check(WidgetCard.IsPowerWidget("PersonalToolsDisplayPower") && !WidgetCard.IsPowerWidget("PersonalToolsHdr"), "wrong widget routing");
    var snapshot = new HdrSnapshot([new("display", "Monitor", true, true, false)], [], null,
        DisplayPower: new([new("PROCESS", @"C:\Program Files\Chrome\chrome.exe", "Video Wake Lock")], DateTimeOffset.Now));
    using var hdr = JsonDocument.Parse(WidgetCard.Create(snapshot, 0, 2));
    Check(hdr.RootElement.GetProperty("body").GetArrayLength() == 1, "power content added to HDR widget");
    Check(!hdr.RootElement.ToString().Contains("chrome.exe"), "power name leaked into HDR widget");
    using var power = JsonDocument.Parse(WidgetCard.CreatePower(snapshot, 0, 1));
    Check(power.RootElement.GetProperty("body")[1].GetProperty("text").GetString() == "[PROCESS] chrome.exe\nVideo Wake Lock", "process name hidden by long path");
    Check(power.RootElement.GetProperty("actions").EnumerateArray().All(a => a.GetProperty("verb").GetString() == "refresh"), "power widget includes HDR controls");
});
Console.WriteLine($"{passed} tests passed.");

if (args.Contains("--integration"))
{
    await DisplayPowerChecks.RunAsync();
    var d = new Displays();
    // Use a real child process, real process polling and pipe, but never modify physical HDR or user settings.
    string exe = Path.Combine(AppContext.BaseDirectory, "HdrTests.exe");
    var store = new ProgramStore([new(Guid.NewGuid(), "Fixture", exe)]);
    string testPipe = HdrWire.PipeName + ".test." + Guid.NewGuid().ToString("N");
    var settings = new SettingsStore();
    await using var controller = new HdrController(d, new Journal(), store, testPipe, settings);
    controller.Start();
    await Task.Delay(1000);
    // The parent uses the same executable. Register a copied apphost, which loads the same DLL.
    string childExe = Path.Combine(AppContext.BaseDirectory, "HdrFixture.exe");
    File.Copy(exe, childExe, true);
    await controller.SaveProgramAsync(store.Items[0] with { Path = childExe });
    using var child = Process.Start(new ProcessStartInfo(childExe, "--fixture") { UseShellExecute = false, CreateNoWindow = true })!;
    var limit = Stopwatch.StartNew();
    while (!controller.Snapshot.Programs.Any(p => p.Running) && limit.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
    Check(controller.Snapshot.Programs.Any(p => p.Running) && d.On, "real process was not detected: " + controller.Snapshot.Error);
    Check(controller.Snapshot.Error is null, "watcher failed: " + controller.Snapshot.Error);
    if (controller.Snapshot.Warning is not null) Console.WriteLine("INFO polling fallback exercised: " + controller.Snapshot.Warning);
    using (var pipe = new NamedPipeClientStream(".", testPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
    {
        await pipe.ConnectAsync(3000);
        await HdrWire.WriteAsync(pipe, new HdrRequest("set", "monitor-a", false), default);
        var state = await HdrWire.ReadAsync<HdrSnapshot>(pipe, default);
        Check(state.Error is null && !state.Monitors[0].Enabled, "IPC manual change failed");
    }
    await child.WaitForExitAsync();
    limit.Restart();
    while (controller.Snapshot.Programs.Any(p => p.Running) && limit.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
    Check(!controller.Snapshot.Programs.Any(p => p.Running) && !d.On, "real process stop/manual persistence failed");
    Console.WriteLine("PASS live process start/stop detection, actual named pipe command, manual override persistence");

    // The master switch releases a running session and ignores new launches until it is back on.
    using (var first = Process.Start(new ProcessStartInfo(childExe, "--fixture") { UseShellExecute = false, CreateNoWindow = true })!)
    {
        limit.Restart();
        while (controller.Snapshot.Active is not [_] && limit.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
        Check(d.On && controller.Snapshot.Active is [{ Programs: ["Fixture"], StartedAt: not null }], "session not reported: " + controller.Snapshot.Error);
        await controller.SetEnabledAsync(false);
        Check(!d.On && settings.Saved is { Enabled: false } && controller.Snapshot is { Enabled: false, Active: [] }, "switching off did not restore");
        await first.WaitForExitAsync();
    }
    using (var second = Process.Start(new ProcessStartInfo(childExe, "--fixture") { UseShellExecute = false, CreateNoWindow = true })!)
    {
        await Task.Delay(1500);
        Check(!d.On, "switched off but still applied");
        await controller.SetEnabledAsync(true);
        Check(d.On, "switching on did not apply to a running program");
        await second.WaitForExitAsync();
    }
    limit.Restart();
    while (d.On && limit.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
    Check(!d.On, "restore after re-enabling failed");
    File.Delete(childExe);
    Console.WriteLine("PASS master switch releases, blocks and re-applies");
}

sealed class Displays : IHdrDisplay
{
    public string Id = "monitor-a";
    public bool On, ThrowAfterWrite, ThrowBeforeWrite, Supported = true;
    public int Writes;
    public HdrMonitor[] Read() => [new(Id, "Display", true, Supported, On)];
    public void Set(string id, bool enabled)
    {
        if (id != Id) throw new IOException("disconnected");
        if (ThrowBeforeWrite) throw new IOException("driver unavailable");
        On = enabled; Writes++;
        if (ThrowAfterWrite) throw new IOException("verification failed");
    }
}
sealed class MultipleDisplays : IHdrDisplay
{
    public bool First, Second, SecondPrimary;
    public HdrMonitor[] Read() => [new("first", "First", !SecondPrimary, true, First), new("second", "Second", SecondPrimary, true, Second)];
    public void Set(string id, bool enabled)
    {
        if (id == "first") First = enabled;
        else if (id == "second") Second = enabled;
        else throw new IOException("unknown monitor");
    }
}
sealed class Journal : IHdrJournal
{
    private string saved = "[]";
    public bool Fail;
    public List<HdrSession> Load() => JsonSerializer.Deserialize<List<HdrSession>>(saved)!;
    public void Save(List<HdrSession> sessions)
    {
        if (Fail) throw new IOException("disk failure");
        saved = JsonSerializer.Serialize(sessions);
    }
}
sealed class SettingsStore : IHdrSettingsStore
{
    public HdrSettings? Saved;
    public HdrSettings Load() => Saved ?? new();
    public void Save(HdrSettings settings) => Saved = settings;
}
sealed class ProgramStore(List<HdrProgram> items) : IHdrProgramStore
{
    public List<HdrProgram> Items = items;
    public List<HdrProgram> Load() => Items;
    public void Save(List<HdrProgram> programs) => Items = programs;
}
