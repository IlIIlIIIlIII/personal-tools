using System.Runtime.InteropServices;
using System.Text.Json;
using PersonalTools;
using PersonalTools.HdrWidget;
using System.Diagnostics;
using System.IO.Pipes;

if (args.Contains("--fixture")) { Thread.Sleep(2500); return; }
if (args.Contains("--fixture-long")) { Thread.Sleep(6000); return; }
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
string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
string Fixture(string name) => File.ReadAllText(Path.Combine(fixtures, name));
var deltaForce = new HdrGame("steam:2507950", "Delta Force", "Steam", @"D:\Steam\steamapps\common\Delta Force");
Test("Steam library folders, current and pre-2021 formats", () =>
{
    var libraries = HdrGameScanner.SteamLibraries(Fixture("libraryfolders.vdf"));
    Check(libraries.SequenceEqual([@"D:\Program Files (x86)\Steam", @"E:\SteamLibrary"]), "libraries: " + string.Join(" | ", libraries));
    Check(HdrGameScanner.SteamLibraries(Fixture("libraryfolders-legacy.vdf")).SequenceEqual([@"E:\SteamLibrary"]), "legacy format");
    var folders = (Dictionary<string, object>)HdrGameScanner.ParseVdf(Fixture("libraryfolders.vdf"))["libraryfolders"];
    Check(((Dictionary<string, object>)folders["1"])["label"] as string == "Games \"SSD\"", "escaped quote");
    foreach (string broken in new[] { "\"a\" {", "\"a\" \"b", "}", "\"a\"" })
    {
        bool rejected = false;
        try { HdrGameScanner.ParseVdf(broken); } catch (FormatException) { rejected = true; }
        Check(rejected, "accepted broken VDF: " + broken);
    }
});
Test("Steam manifests: name, install folder, UTF-8", () =>
{
    Check(HdrGameScanner.ParseSteamManifest(Fixture("appmanifest_1888930.acf")) is { AppId: "1888930", Name: "The Last of Us™ Part I", InstallDir: "The Last of Us Part I" }, "manifest");
    Check(HdrGameScanner.ParseSteamManifest(Fixture("appmanifest_2507950.acf")) is { AppId: "2507950", InstallDir: "Delta Force" }, "nested blocks");
    Check(HdrGameScanner.ParseSteamManifest("\"AppState\" { \"appid\" \"1\" }") is null, "manifest without installdir accepted");
});
Test("Epic manifests keep finished base games only", () =>
{
    Check(HdrGameScanner.ParseEpicManifest(Fixture("epic-game.item")) is { Name: "Alan Wake 2", Source: "Epic", Folder: @"C:\Program Files\Epic Games\AlanWake2",
        Icon: @"C:\Program Files\Epic Games\AlanWake2\AlanWake2.exe" }, "game");
    foreach (string name in new[] { "epic-engine.item", "epic-dlc.item", "epic-incomplete.item" })
        Check(HdrGameScanner.ParseEpicManifest(Fixture(name)) is null, "listed " + name);
});
Test("library scan: Steam libraries, store records, store launchers excluded, Game Bar merged", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "HdrGames-" + Guid.NewGuid().ToString("N"));
    string Dir(params string[] parts) { string path = Path.Combine([root, .. parts]); Directory.CreateDirectory(path); return path; }
    string Touch(params string[] parts) { string path = Path.Combine([root, .. parts]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ""); return path; }
    try
    {
        string steam = Dir("Steam", "steamapps"), library = Dir("Library", "steamapps");
        File.WriteAllText(Path.Combine(steam, "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\" \"" + Path.GetDirectoryName(steam)!.Replace("\\", "\\\\")
            + "\" } \"1\" { \"path\" \"" + Path.GetDirectoryName(library)!.Replace("\\", "\\\\") + "\" } }");
        foreach (string name in new[] { "appmanifest_2507950.acf", "appmanifest_228980.acf" }) File.Copy(Path.Combine(fixtures, name), Path.Combine(steam, name));
        File.Copy(Path.Combine(fixtures, "appmanifest_1888930.acf"), Path.Combine(library, "appmanifest_1888930.acf"));
        string delta = Dir("Steam", "steamapps", "common", "Delta Force");
        Dir("Steam", "steamapps", "common", "Steamworks Shared");
        string tlou = Dir("Library", "steamapps", "common", "The Last of Us Part I");
        string launcherExe = Touch("Steam", "steamapps", "common", "Delta Force", "Launcher", "df_launcher.exe");
        // Ubisoft Connect keeps its games under its own folder; the EA app folder holds the store launcher.
        string ubisoft = Dir("Ubisoft");
        Touch("Ubisoft", "upc.exe");
        string anno = Dir("Ubisoft", "games", "Anno 1800");
        string ea = Dir("EA Desktop");
        Touch("EA Desktop", "EA Desktop", "EADesktop.exe");
        string vanguard = Dir("Riot Vanguard");
        string manifests = Dir("Epic", "Manifests"), alan = Dir("Epic Games", "AlanWake2");
        File.WriteAllText(Path.Combine(manifests, "dill.item"), Fixture("epic-game.item").Replace(@"C:\\Program Files\\Epic Games\\AlanWake2", alan.Replace("\\", "\\\\")));
        Dir("XboxGames", "Forza Horizon 5", "Content");
        Dir("XboxGames", "GameSave");
        string witcher = Dir("GOG", "The Witcher 3");
        string indie = Touch("Indie", "Game", "indie.exe");
        var scanner = new HdrGameScanner
        {
            SteamRoot = () => Path.GetDirectoryName(steam)!.Replace('\\', '/'),
            Uninstall = () =>
            [
                new("Steam App 2507950", "델타 포스", "TiMi Studio Group", delta, Path.Combine(root, "missing.ico") + ",0"),
                new("Uplay", "Ubisoft Connect", "Ubisoft", ubisoft.Replace('\\', '/') + "/", null),
                new("Uplay Install 4553", "Anno 1800", "Ubisoft", anno.Replace('\\', '/') + "/", null),
                new("Uplay Install 4553", "Anno 1800", "Ubisoft", anno + "\\", null),
                new("EA app", "EA app", "Electronic Arts", ea, null),
                new("{0E5A0B31}", "EA Helper", "Electronic Arts", ea, null),
                new("{5B2E2C1F}", "Bundle", "Electronic Arts", Path.Combine(root, "Epic Games"), null),
                new("Riot Vanguard", "Riot Vanguard", "Riot Games, Inc", vanguard, null),
                new("Notepad++", "Notepad++", "Notepad++ Team", Dir("Notepad++"), null),
            ],
            EpicManifests = manifests,
            XboxRoots = () => [Path.Combine(root, "XboxGames"), Path.Combine(root, "NoXbox")],
            Gog = () => [new("1207664663", "The Witcher 3", witcher, null), new("1", "Hearts of Stone", witcher, null, "1207664663")],
            GameBar = () => [launcherExe, indie, Path.Combine(root, "missing.exe"), Path.Combine(ubisoft, "upc.exe")],
        };
        var games = scanner.Scan();
        string List() => string.Join(", ", games.Select(g => $"{g.Key}={g.Source}:{g.Folder}"));
        Check(games.Length == 7, "count: " + List());
        Check(games.Single(g => g.Key == "steam:2507950") is { Name: "델타 포스", Icon: null } d && d.Folder == delta, "localized Steam entry: " + List());
        Check(games.Any(g => g.Key == "steam:1888930" && g.Name == "The Last of Us™ Part I" && g.Folder == tlou), "second library: " + List());
        Check(!games.Any(g => g.Key == "steam:228980"), "redistributables listed");
        Check(games.Single(g => g.Source == "Ubisoft") is { Key: "ubisoft:4553" } u && u.Folder == anno, "Ubisoft game: " + List());
        Check(!games.Any(g => new[] { ubisoft, ea, vanguard, Path.Combine(root, "Epic Games") }.Contains(g.Folder)), "store or container listed: " + List());
        Check(games.Any(g => g.Source == "Epic" && g.Folder == alan) && games.Any(g => g.Source == "Xbox" && g.Name == "Forza Horizon 5"), "Epic or Xbox: " + List());
        Check(games.Single(g => g.Source == "GOG").Folder == witcher, "GOG");
        Check(games.Single(g => g.Source == "Windows") is { Folder: var folder } && folder == Path.GetDirectoryName(indie), "Game Bar entry: " + List());
    }
    finally { Directory.Delete(root, true); }
});
Test("folder match covers the game's own launcher, not store launchers or sibling folders", () =>
{
    var m = new HdrMatcher([], [deltaForce]);
    Check(m.Find(deltaForce.Folder + @"\Launcher\df_launcher.exe")?.Game == deltaForce, "launcher in the game folder");
    Check(m.Find(@"d:\steam\STEAMAPPS\common\delta force\Game\client.exe") is not null, "case sensitive");
    Check(m.Find(@"D:\Steam\steamapps\common\Delta Force 2\game.exe") is null, "sibling folder with the same prefix");
    Check(m.Find(deltaForce.Folder + @"\steamwebhelper.exe") is null, "store launcher matched");
    Check(new HdrMatcher([], [deltaForce], [deltaForce.Key]).Find(deltaForce.Folder + @"\x.exe") is null, "switched-off game matched");
    var nested = new HdrGame("test:tool", "Tool", "Steam", deltaForce.Folder + @"\Tools");
    Check(new HdrMatcher([], [deltaForce, nested]).Find(nested.Folder + @"\tool.exe")?.Game == nested, "deepest folder must win");
});
Test("manual program inside a game folder claims the game", () =>
{
    string client = deltaForce.Folder + @"\Game\client.exe", launcher = deltaForce.Folder + @"\Launcher\df_launcher.exe";
    var manual = new HdrProgram(Guid.NewGuid(), "델타포스", client);
    var claimed = new HdrMatcher([manual], [deltaForce], [deltaForce.Key]);
    Check(claimed.Find(launcher) is { Program: null, Game: { } g } && g == deltaForce, "manual on did not cover the game's launcher");
    Check(claimed.Find(client) is { Program: { } p } && p.Id == manual.Id, "exact match lost");
    var off = new HdrMatcher([manual with { Enabled = false }], [deltaForce]);
    Check(off.Find(launcher) is null && off.Find(client) is null, "manual off did not switch the game off");
    Check(HdrMatcher.Claim(deltaForce, [manual])?.Id == manual.Id && HdrMatcher.Claim(deltaForce, [game]) is null, "claim");
    var gamesOff = new HdrMatcher([manual], []);
    Check(gamesOff.Find(launcher) is null && gamesOff.Find(client) is not null, "folder matched with games switched off");
});
Test("launcher before game hands over without a restore", () =>
{
    var d = new Displays(); var a = new HdrAutomation(d, new Journal());
    var m = new HdrMatcher([], [deltaForce]);
    var launcher = new HdrProcess("20:1", deltaForce.Folder + @"\Launcher\df_launcher.exe");
    var client = new HdrProcess("21:2", deltaForce.Folder + @"\Game\client.exe");
    a.Reconcile([launcher], m); Check(d.On, "launcher did not turn HDR on");
    a.Reconcile([launcher, client], m); a.Reconcile([client], m);
    Check(d.On && d.Writes == 1, "restored between launcher and game");
    Check(a.Sessions.Single().Owners.SequenceEqual(["21:2"]), "ownership not handed to the game");
    a.Reconcile([], m); Check(!d.On && d.Writes == 2, "not restored after the game");
    a.Reconcile([new("22:3", @"C:\Steam\steam.exe")], new HdrMatcher([], [deltaForce with { Folder = @"C:\Steam" }]));
    Check(!d.On, "store launcher started a session");
});
Test("settings written before game detection keep defaults", () =>
{
    var old = JsonSerializer.Deserialize<HdrSettings>("{\"Enabled\":false}")!;
    Check(old is { Enabled: false, Games: true, DisabledGames: null }, "old settings.json lost defaults");
    var round = JsonSerializer.Deserialize<HdrSettings>(JsonSerializer.Serialize(new HdrSettings(true, false, ["steam:1"])))!;
    Check(round is { Games: false, DisabledGames: ["steam:1"] }, "round trip");
});
Test("process list matches Process.GetProcesses and reads sessions only when needed", () =>
{
    var list = new HdrProcessList();
    using var self = Process.GetCurrentProcess();
    Check(list.Read(_ => false).Single(e => e.Id == self.Id) is { Name: "HdrTests", Session: null }, "no basic list, or sessions read that nobody needed");
    Check(list.Read(name => name == "HdrTests").Single(e => e.Id == self.Id).Session == self.SessionId, "own session");
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fixture") { UseShellExecute = false, CreateNoWindow = true })!;
    try
    {
        var all = list.Read(_ => true).ToDictionary(e => e.Id);
        Check(all[child.Id] is { Name: "HdrTests" } started && started.Session == self.SessionId, "process started between polls");
        int same = 0;
        foreach (var p in Process.GetProcesses()) using (p) if (all.TryGetValue(p.Id, out var e) && e.Name == p.ProcessName && e.Session == p.SessionId) same++;
        Check(same > all.Count * 9 / 10, $"only {same} of {all.Count} match Process.GetProcesses");
    }
    finally { if (!child.HasExited) child.Kill(); }
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
    await using var controller = new HdrController(d, new Journal(), store, testPipe, settings, () => []);
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

    await controller.SetTaskbarMonitorAsync("monitor-a", false);
    await controller.SetTaskbarButtonsAsync(false);
    Check(settings.Saved is { TaskbarButtons: false, TaskbarHidden: ["monitor-a"] } && controller.Snapshot is { TaskbarButtons: false, TaskbarHidden: ["monitor-a"] },
        "taskbar settings not saved or published");
    await controller.SetTaskbarMonitorAsync("MONITOR-A", true);
    await controller.SetTaskbarButtonsAsync(true);
    Check(settings.Saved is { TaskbarButtons: true, TaskbarHidden: [] } && controller.Snapshot.TaskbarButtons, "taskbar settings not restored");
    Console.WriteLine("PASS taskbar button settings are saved and published");

    async Task Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception(what); await Task.Delay(50); }
    }
    Process Run(string exe, string argument) => Process.Start(new ProcessStartInfo(exe, argument) { UseShellExecute = false, CreateNoWindow = true })!;
    // A detected game folder with a full copy of this test app: its own launcher turns HDR on before the game starts.
    string gameDir = Path.Combine(AppContext.BaseDirectory, "GameFixture");
    foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories).Where(f => !HdrGameScanner.Under(f, gameDir)))
    {
        string target = Path.Combine(gameDir, Path.GetRelativePath(AppContext.BaseDirectory, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, true);
    }
    string launcherExe = Path.Combine(gameDir, "FixtureLauncher.exe"), gameExe = Path.Combine(gameDir, "FixtureGame.exe");
    File.Copy(Path.Combine(gameDir, "HdrTests.exe"), launcherExe, true);
    File.Copy(Path.Combine(gameDir, "HdrTests.exe"), gameExe, true);
    var fixtureGame = new HdrGame("test:fixture", "Fixture Game", "Steam", HdrGameScanner.Normalize(gameDir));
    var gameDisplay = new Displays();
    var gameSettings = new SettingsStore();
    await using (var games = new HdrController(gameDisplay, new Journal(), new ProgramStore([]), testPipe + ".games", gameSettings, () => [fixtureGame]))
    {
        games.Start();
        await Until(() => games.Snapshot.Games is [_], "detected games not published");
        using (var launcher = Run(launcherExe, "--fixture"))
        {
            await Until(() => gameDisplay.On, "game launcher did not turn HDR on: " + games.Snapshot.Error);
            Check(games.Snapshot.Games is [{ Running: true, RunningExe: "FixtureLauncher.exe" }], "launcher not shown on the game row");
            Check(games.Snapshot.Active is [{ Programs: ["Fixture Game"] }], "session does not name the game");
            using var client = Run(gameExe, "--fixture-long");
            await launcher.WaitForExitAsync();
            await Task.Delay(1500);
            Check(gameDisplay.On && gameDisplay.Writes == 1, "restored when the launcher exited");
            await games.SetGameEnabledAsync(fixtureGame.Key, false);
            Check(!gameDisplay.On && gameSettings.Saved?.DisabledGames is ["test:fixture"], "per-game switch did not release");
            await games.SetGameEnabledAsync(fixtureGame.Key, true);
            Check(gameDisplay.On, "per-game switch did not re-apply to the running game");
            await client.WaitForExitAsync();
        }
        await Until(() => !gameDisplay.On, "not restored after the game exited");
        await games.SetGamesEnabledAsync(false);
        using (var again = Run(launcherExe, "--fixture"))
        {
            await Task.Delay(1500);
            Check(!gameDisplay.On && games.Snapshot is { GamesEnabled: false, Games: [{ Running: false }] }, "folder matched with installed games switched off");
            await again.WaitForExitAsync();
        }
    }
    for (int attempt = 0; Directory.Exists(gameDir); attempt++)
    {
        try { Directory.Delete(gameDir, true); }
        catch (Exception) when (attempt < 20) { await Task.Delay(250); }
    }
    Console.WriteLine("PASS game folder: launcher before game, hand-over, per-game and installed-games switches");

    var clock = Stopwatch.StartNew();
    int described = 0;
    foreach (var each in Process.GetProcesses()) using (each) if (HdrController.Describe(each.Id) is not null) described++;
    Console.WriteLine($"INFO describing all {described} processes once took {clock.ElapsedMilliseconds} ms; later ticks only describe new processes");
    clock.Restart();
    var found = new HdrGameScanner().Scan();
    clock.Stop();
    Check(found.All(g => Directory.Exists(g.Folder)), "detected a missing folder");
    Check(!found.Any(g => Directory.EnumerateFiles(g.Folder, "*.exe").Any(f => HdrGameScanner.Launchers.Contains(Path.GetFileName(f)))), "detected a store launcher folder");
    Console.WriteLine($"PASS this PC's libraries ({clock.ElapsedMilliseconds} ms): " + string.Join(", ", found.Select(g => $"{g.Name} [{g.Source}]")));

    // Polling cost with this PC's libraries, where every process in the session is a candidate, against the
    // registered-names-only polling of earlier builds. The display stays fake.
    foreach (bool withGames in new[] { false, true })
    {
        await using var real = new HdrController(new Displays(), new Journal(), new ProgramStore([]), testPipe + ".cpu" + withGames, new SettingsStore(),
            withGames ? null : () => []);
        real.Start();
        await Until(() => real.Snapshot.GamesScannedAt is not null, "libraries not scanned");
        await Task.Delay(2000);
        using var self = Process.GetCurrentProcess();
        var before = self.TotalProcessorTime;
        clock.Restart();
        await Task.Delay(10000);
        self.Refresh();
        Console.WriteLine($"INFO polling with {real.Snapshot.Games?.Length} detected games: {(self.TotalProcessorTime - before).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100:N2}% of one core");
    }
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
