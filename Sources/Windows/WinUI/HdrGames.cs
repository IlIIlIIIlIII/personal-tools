using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PersonalTools;

// One Apps & features entry; Key is its registry subkey name ("Uplay Install 4553", "Steam App 1295660").
public sealed record HdrUninstallEntry(string Key, string? Name, string? Publisher, string? Location, string? Icon);
public sealed record HdrGogEntry(string Id, string? Name, string? Path, string? Exe, string? DependsOn = null);
public sealed record HdrSteamApp(string AppId, string Name, string InstallDir);

// Finds installed games, read-only: Steam manifests, store install records and the Game Bar's game list.
// Every source is replaceable so tests run against fixtures instead of this PC's libraries.
public sealed class HdrGameScanner
{
    // Store launchers stay open for the whole session, so they must never keep HDR on, and a folder holding one is a store, not a game.
    public static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe", "Battle.net.exe", "EADesktop.exe",
        "EABackgroundService.exe", "Origin.exe", "GalaxyClient.exe", "upc.exe", "UbisoftConnect.exe", "UplayWebCore.exe", "RiotClientServices.exe",
        "RiotClientUx.exe", "XboxPcApp.exe", "Playnite.DesktopApp.exe", "Playnite.FullscreenApp.exe",
    };
    // Steam tools that are installed like games: redistributables, runtimes, Proton and SteamVR.
    private static readonly HashSet<string> SteamTools = ["228980", "1070560", "1391110", "1628350", "1493710", "1826330", "1161040", "250820"];
    private static readonly Regex NotGame = new(@"(?i)^(ea app|ea desktop|origin|battle\.net|ubisoft connect|uplay|riot client|epic games launcher|gog galaxy)$|launcher|social club|vanguard|anti[- ]?cheat|redistributable|runtime");

    public Func<string?> SteamRoot { get; init; } = () => Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
    public Func<IEnumerable<HdrUninstallEntry>> Uninstall { get; init; } = ReadUninstall;
    public string EpicManifests { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
    public Func<IEnumerable<string>> XboxRoots { get; init; } = () => DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => Path.Combine(d.RootDirectory.FullName, "XboxGames"));
    public Func<IEnumerable<HdrGogEntry>> Gog { get; init; } = ReadGog;
    public Func<IEnumerable<string>> GameBar { get; init; } = ReadGameBar;

    public HdrGame[] Scan()
    {
        var uninstall = Try(() => Uninstall().ToList());
        var found = new List<HdrGame>();
        found.AddRange(Try(() => Steam(uninstall)));
        found.AddRange(Try(() => Stores(uninstall)));
        found.AddRange(Try(Epic));
        found.AddRange(Try(Xbox));
        found.AddRange(Try(GogGames));
        var games = new List<HdrGame>();
        foreach (var game in found)
        {
            // A folder that holds other game folders is a store's (Ubisoft Connect keeps games\ under its own folder).
            if (Broad(game.Folder) || HasLauncher(game.Folder) || found.Any(other => !Same(other.Folder, game.Folder) && Under(other.Folder, game.Folder))) continue;
            if (games.Any(g => Same(g.Folder, game.Folder))) continue; // The first source wins.
            games.Add(game with { Icon = game.Icon is { } icon && File.Exists(icon) ? icon : null });
        }
        // The Game Bar list is undocumented, so it only adds games the libraries missed.
        foreach (string raw in Try(() => GameBar().ToList()))
        {
            string exe;
            try { exe = Path.GetFullPath(raw); } catch { continue; }
            string folder = Path.GetDirectoryName(exe) ?? "";
            if (games.Any(g => Under(exe, g.Folder)) || Launchers.Contains(Path.GetFileName(exe)) || !File.Exists(exe)) continue;
            if (folder.Length == 0 || Broad(folder) || HasLauncher(folder) || games.Any(g => Same(g.Folder, folder))) continue;
            games.Add(new("gamebar:" + exe.ToLowerInvariant(), HdrController.DisplayName(exe), "Windows", Normalize(folder), exe));
        }
        return games.ToArray();
    }

    private List<HdrGame> Steam(List<HdrUninstallEntry> uninstall)
    {
        var apps = new Dictionary<string, (string Name, string Folder)>();
        if (SteamRoot() is { Length: > 0 } configured)
        {
            string root = Normalize(configured);
            var libraries = new List<string> { root };
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf)) libraries.AddRange(SteamLibraries(File.ReadAllText(vdf)).Select(Normalize));
            foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;
                foreach (string manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    try
                    {
                        if (ParseSteamManifest(File.ReadAllText(manifest)) is not { } app || IsSteamTool(app.AppId, app.Name)) continue;
                        string folder = Path.Combine(steamapps, "common", app.InstallDir);
                        if (Directory.Exists(folder)) apps.TryAdd(app.AppId, (app.Name, Normalize(folder)));
                    }
                    catch { }
                }
            }
        }
        var result = new List<HdrGame>();
        // Steam also lists each game in Apps & features, with the store's localized name and an icon.
        var listed = uninstall.Where(e => e.Key.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Key[10..].Trim()).ToDictionary(g => g.Key, g => g.First());
        foreach (var (id, entry) in listed)
        {
            if (IsSteamTool(id, entry.Name ?? "")) continue;
            if (apps.TryGetValue(id, out var app)) result.Add(new("steam:" + id, Text(entry.Name) ?? app.Name, "Steam", app.Folder, IconPath(entry.Icon)));
            else if (Text(entry.Name) is { } name && Text(entry.Location) is { } location && Directory.Exists(location))
                result.Add(new("steam:" + id, name, "Steam", Normalize(location), IconPath(entry.Icon)));
        }
        foreach (var (id, app) in apps)
            if (!listed.ContainsKey(id)) result.Add(new("steam:" + id, app.Name, "Steam", app.Folder));
        return result;
    }
    private static bool IsSteamTool(string id, string name) => SteamTools.Contains(id)
        || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) || name.Equals("SteamVR", StringComparison.OrdinalIgnoreCase);

    // Stores without a manifest folder of their own register each game in Apps & features.
    private static List<HdrGame> Stores(List<HdrUninstallEntry> uninstall)
    {
        var result = new List<HdrGame>();
        foreach (var entry in uninstall.Where(e => !e.Key.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)))
        {
            string publisher = entry.Publisher ?? "";
            var (source, key) = entry.Key.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase) ? ("Ubisoft", "ubisoft:" + entry.Key[14..].Trim())
                : publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) ? ("EA", "uninstall:" + entry.Key)
                : publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ? ("Battle.net", "uninstall:" + entry.Key)
                : publisher.StartsWith("Riot Games", StringComparison.OrdinalIgnoreCase) ? ("Riot", "uninstall:" + entry.Key)
                : publisher.StartsWith("Rockstar Games", StringComparison.OrdinalIgnoreCase) ? ("Rockstar", "uninstall:" + entry.Key)
                : ("", "");
            if (source.Length == 0 || Text(entry.Name) is not { } name || NotGame.IsMatch(name) || Text(entry.Location) is not { } location) continue;
            if (Directory.Exists(location)) result.Add(new(key, name, source, Normalize(location), IconPath(entry.Icon)));
        }
        return result;
    }

    private List<HdrGame> Epic()
    {
        var result = new List<HdrGame>();
        if (!Directory.Exists(EpicManifests)) return result;
        foreach (string file in Directory.EnumerateFiles(EpicManifests, "*.item"))
        {
            try { if (ParseEpicManifest(File.ReadAllText(file)) is { } game && Directory.Exists(game.Folder)) result.Add(game); }
            catch { }
        }
        return result;
    }

    // The Xbox app installs each game as <drive>:\XboxGames\<name>\Content.
    private List<HdrGame> Xbox()
    {
        var result = new List<HdrGame>();
        foreach (string root in XboxRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                string content = Path.Combine(folder, "Content");
                if (!Directory.Exists(content)) continue;
                string? exe = null;
                try { exe = Directory.EnumerateFiles(content, "*.exe").FirstOrDefault(); } catch { }
                result.Add(new("xbox:" + Path.GetFileName(folder).ToLowerInvariant(), Path.GetFileName(folder), "Xbox", Normalize(folder), exe));
            }
        }
        return result;
    }

    private List<HdrGame> GogGames() => Gog()
        .Where(g => string.IsNullOrWhiteSpace(g.DependsOn) && Text(g.Name) is not null && Text(g.Path) is { } path && Directory.Exists(path))
        .Select(g => new HdrGame("gog:" + g.Id, g.Name!.Trim(), "GOG", Normalize(g.Path!), g.Exe)).ToList();

    // Valve KeyValues text (libraryfolders.vdf, appmanifest_*.acf): quoted keys with a string or a { } block.
    public static Dictionary<string, object> ParseVdf(string text)
    {
        int i = 0;
        return Block(text, ref i, true);
    }
    private static Dictionary<string, object> Block(string text, ref int i, bool top)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? key = Token(text, ref i, out bool brace);
            if (key is null) return top ? result : throw new FormatException("VDF 블록이 닫히지 않았습니다.");
            if (brace) return key == "}" && !top ? result : throw new FormatException("VDF 괄호가 맞지 않습니다.");
            string? value = Token(text, ref i, out brace);
            if (value is null) throw new FormatException("VDF 값이 없습니다.");
            if (!brace) result[key] = value;
            else if (value == "{") result[key] = Block(text, ref i, false);
            else throw new FormatException("VDF 괄호가 맞지 않습니다.");
        }
    }
    private static string? Token(string text, ref int i, out bool brace)
    {
        brace = false;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c is '{' or '}') { i++; brace = true; return c.ToString(); }
            var token = new StringBuilder();
            if (c == '"')
            {
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) token.Append(text[++i] switch { 'n' => '\n', 't' => '\t', var other => other });
                    else token.Append(text[i]);
                }
                if (i >= text.Length) throw new FormatException("VDF 문자열이 닫히지 않았습니다.");
                i++;
                return token.ToString();
            }
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) token.Append(text[i++]);
            return token.ToString();
        }
        return null;
    }
    // Current files hold { "path" ... } per library; files from before 2021 map the index straight to the path.
    public static string[] SteamLibraries(string vdf)
    {
        var root = ParseVdf(vdf);
        if (root.Values.FirstOrDefault() is not Dictionary<string, object> folders) return [];
        return folders.Where(f => int.TryParse(f.Key, out _))
            .Select(f => f.Value is Dictionary<string, object> library ? library.GetValueOrDefault("path") as string : f.Value as string)
            .OfType<string>().Where(p => p.Length > 0).ToArray();
    }
    public static HdrSteamApp? ParseSteamManifest(string acf)
    {
        if (ParseVdf(acf).Values.FirstOrDefault() is not Dictionary<string, object> app) return null;
        return app.GetValueOrDefault("appid") is string id && app.GetValueOrDefault("installdir") is string dir && dir.Length > 0
            ? new(id, app.GetValueOrDefault("name") as string is { Length: > 0 } name ? name : dir, dir) : null;
    }
    // Epic lists engines, plug-ins and DLC next to games; only a finished base game counts.
    public static HdrGame? ParseEpicManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array
            && !categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "games")) return null;
        string? app = Str("AppName"), main = Str("MainGameAppName");
        if (app is not null && main is not null && !string.Equals(app, main, StringComparison.OrdinalIgnoreCase)) return null;
        if (Text(Str("DisplayName")) is not { } name || Text(Str("InstallLocation")) is not { } folder) return null;
        string? exe = Text(Str("LaunchExecutable"));
        return new("epic:" + (app ?? name).ToLowerInvariant(), name, "Epic", Normalize(folder), exe is null ? null : Path.Combine(folder, exe));
    }

    public static string Normalize(string path) => Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    // True when path lies inside folder; "Delta Force 2" is not inside "Delta Force".
    public static bool Under(string path, string folder) => path.Length > folder.Length + 1 && path[folder.Length] == '\\'
        && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    // DisplayIcon looks like "C:\path\game.exe,0" or a quoted .ico path.
    private static string? IconPath(string? icon)
    {
        if (Text(icon) is not { } value) return null;
        value = value.Trim('"');
        int comma = value.LastIndexOf(',');
        if (comma > 2 && int.TryParse(value[(comma + 1)..], out _)) value = value[..comma].Trim('"');
        try { return Path.GetFullPath(value); } catch { return null; }
    }
    private static bool Broad(string folder)
    {
        if (Path.GetPathRoot(folder) is { } root && Same(root.TrimEnd('\\'), folder)) return true;
        return new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
        }.Select(Environment.GetFolderPath).Any(p => p.Length > 0 && Same(p.TrimEnd('\\'), folder));
    }
    private static bool HasLauncher(string folder)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(folder).Prepend(folder))
                foreach (string exe in Directory.EnumerateFiles(dir, "*.exe"))
                    if (Launchers.Contains(Path.GetFileName(exe))) return true;
        }
        catch { }
        return false;
    }
    private static List<T> Try<T>(Func<IEnumerable<T>> read)
    {
        try { return read().ToList(); }
        catch { return []; }
    }

    private static IEnumerable<HdrUninstallEntry> ReadUninstall()
    {
        var result = new List<HdrUninstallEntry>();
        foreach (var (hive, path) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) continue;
            foreach (string name in key.GetSubKeyNames())
            {
                try
                {
                    using var entry = key.OpenSubKey(name);
                    if (entry is not null) result.Add(new(name, entry.GetValue("DisplayName") as string, entry.GetValue("Publisher") as string,
                        entry.GetValue("InstallLocation") as string, entry.GetValue("DisplayIcon") as string));
                }
                catch { }
            }
        }
        return result;
    }
    private static IEnumerable<HdrGogEntry> ReadGog()
    {
        var result = new List<HdrGogEntry>();
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (key is null) return result;
        foreach (string id in key.GetSubKeyNames())
        {
            try
            {
                using var game = key.OpenSubKey(id);
                if (game is not null) result.Add(new(id, game.GetValue("gameName") as string, game.GetValue("path") as string, game.GetValue("exe") as string, game.GetValue("dependsOn") as string));
            }
            catch { }
        }
        return result;
    }
    // Type 1 marks an entry the Game Bar treats as a game.
    private static IEnumerable<string> ReadGameBar()
    {
        var result = new List<string>();
        using var key = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
        if (key is null) return result;
        foreach (string name in key.GetSubKeyNames())
        {
            try
            {
                using var entry = key.OpenSubKey(name);
                if (entry?.GetValue("MatchedExeFullPath") is string { Length: > 0 } path && entry.GetValue("Type") is null or 1) result.Add(path);
            }
            catch { }
        }
        return result;
    }
}

// Decides which running processes keep HDR on. A manual program matches its exact executable; a detected game
// matches any process under its folder except store launchers. A manual program inside a game folder claims
// the game, so its switch decides for the game's launcher too and the game's own switch is not used.
public sealed class HdrMatcher
{
    public sealed record Hit(HdrGame? Game, HdrProgram? Program);
    private readonly Dictionary<string, HdrProgram> exact;
    private readonly (HdrGame Game, bool Enabled)[] folders;
    public HdrMatcher(IReadOnlyCollection<HdrProgram> programs, IReadOnlyCollection<HdrGame>? games = null, IReadOnlyCollection<string>? disabledGames = null)
    {
        exact = programs.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var off = disabledGames?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        // The deepest folder wins when one game folder lies inside another.
        folders = (games ?? []).Select(g => (g, Claim(g, programs) is { } claim ? claim.Enabled : !off.Contains(g.Key)))
            .OrderByDescending(f => f.g.Folder.Length).ToArray();
    }
    public static HdrProgram? Claim(HdrGame game, IEnumerable<HdrProgram> programs)
    {
        var claims = programs.Where(p => HdrGameScanner.Under(p.Path, game.Folder)).ToArray();
        return claims.FirstOrDefault(p => p.Enabled) ?? claims.FirstOrDefault();
    }
    public Hit? Find(string path)
    {
        exact.TryGetValue(path, out var program);
        HdrGame? game = null;
        bool gameOn = false;
        if (!HdrGameScanner.Launchers.Contains(Path.GetFileName(path)))
            foreach (var (candidate, enabled) in folders)
                if (HdrGameScanner.Under(path, candidate.Folder)) { game = candidate; gameOn = enabled; break; }
        // An exact manual entry decides for its own executable, on or off.
        if (program is not null) return program.Enabled ? new(game, program) : null;
        return game is not null && gameOn ? new(game, null) : null;
    }
}
