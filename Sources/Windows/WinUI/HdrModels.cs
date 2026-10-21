using System.Text.Json;

namespace PersonalTools;

public sealed record HdrMonitor(string Id, string Name, bool Primary, bool Supported, bool Enabled, string? Error = null, bool Internal = false);
public sealed record HdrProgram(Guid Id, string Name, string Path, bool Enabled = true);
public sealed record HdrProcess(string Key, string Path);
public sealed record HdrProgramStatus(HdrProgram Program, bool Running);
// One monitor whose HDR automation owns a pending restore, with the programs keeping it.
public sealed record HdrActive(string MonitorId, bool Original, bool Manual, DateTimeOffset? StartedAt, string[] Programs);
// DisplayPower stays for the widget wire format; the app no longer fills it.
public sealed record HdrSnapshot(HdrMonitor[] Monitors, HdrProgramStatus[] Programs, string? Error, string? Warning = null, DisplayPowerSnapshot? DisplayPower = null,
    bool Enabled = true, HdrActive[]? Active = null);
public sealed record HdrRequest(string Command, string? MonitorId = null, bool Enabled = false);
public sealed record HdrCandidate(string Name, string Path, bool Launcher);
public sealed record HdrSettings(bool Enabled = true);

public interface IHdrDisplay
{
    HdrMonitor[] Read();
    void Set(string id, bool enabled);
}

public sealed class HdrSession
{
    public string MonitorId { get; set; } = "";
    public bool Original { get; set; }
    public bool Manual { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? Failure { get; set; }
    public List<string> Owners { get; set; } = [];
}

public interface IHdrJournal
{
    List<HdrSession> Load();
    void Save(List<HdrSession> sessions);
}

internal interface IHdrProgramStore
{
    List<HdrProgram> Load();
    void Save(List<HdrProgram> programs);
}
internal sealed class HdrProgramStore : IHdrProgramStore
{
    public List<HdrProgram> Load() => HdrFiles.Read<List<HdrProgram>>("programs.json", () => []);
    public void Save(List<HdrProgram> programs) => HdrFiles.Write("programs.json", programs);
}

internal interface IHdrSettingsStore
{
    HdrSettings Load();
    void Save(HdrSettings settings);
}
internal sealed class HdrSettingsStore : IHdrSettingsStore
{
    public HdrSettings Load() => HdrFiles.Read("settings.json", () => new HdrSettings());
    public void Save(HdrSettings settings) => HdrFiles.Write("settings.json", settings);
}

internal static class HdrFiles
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalTools", "Hdr");
    public static T Read<T>(string name, Func<T> empty)
    {
        var path = Path.Combine(DirectoryPath, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new IOException("HDR 설정 파일이 비어 있습니다.") : empty();
    }
    public static void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, name);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }
}

internal sealed class HdrJournal : IHdrJournal
{
    private string? lastSaved;
    public List<HdrSession> Load() => HdrFiles.Read<List<HdrSession>>("sessions.json", () => []);
    public void Save(List<HdrSession> sessions)
    {
        string serialized = JsonSerializer.Serialize(sessions);
        if (lastSaved == serialized) return;
        HdrFiles.Write("sessions.json", sessions);
        lastSaved = serialized;
    }
}
