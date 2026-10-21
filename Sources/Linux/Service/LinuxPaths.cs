using System.Text.Json;
namespace PersonalTools;
public static class LinuxPaths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Root(string variable, string fallback) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathRooted(value) ? value : Path.Combine(Home, fallback);
    public static string Config => Path.Combine(Root("XDG_CONFIG_HOME", ".config"), "personal-tools");
    public static string Data => Path.Combine(Root("XDG_DATA_HOME", ".local/share"), "personal-tools");
    public static string State => Path.Combine(Root("XDG_STATE_HOME", ".local/state"), "personal-tools");
    public static string Cache => Path.Combine(Root("XDG_CACHE_HOME", ".cache"), "personal-tools");
    public static string Runtime => Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? throw new IOException("사용자 XDG_RUNTIME_DIR 세션이 필요합니다."), "personal-tools");
    public static string Bridge => Path.Combine(Config, "antigravity");
    public static string ClipboardConfig => Path.Combine(Config, "clipboard");
    public static string Archive => Path.Combine(Data, "clipboard");
    public static string Hdr => Path.Combine(State, "hdr");
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static void Ensure()
    {
        foreach (var dir in new[] { Config, Data, State, Cache, Runtime, Bridge, ClipboardConfig, Archive, Hdr })
        { Directory.CreateDirectory(dir); File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
    public static T Read<T>(string path, T fallback) => Read(path, () => fallback);
    public static T Read<T>(string file, Func<T> fallback) => File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("설정 파일이 비어 있습니다.") : fallback();
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { JsonSerializer.Serialize(output, value, Json); output.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
}
public sealed record ClipImageTags(string[] Tags, string Description, string Text, string Model, DateTimeOffset CreatedAt);
public sealed record ClipMime(string Type, string RelativePath, long Length);
public sealed record MimePayload(string Type, byte[] Bytes);
public sealed partial class ClipboardStore
{
    public async Task<bool> SetImageTagsAsync(string id, string hash, ClipImageTags? tags, string state, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var item = entries.FirstOrDefault(x => x.Id == id && x.ContentHash == hash);
            if (item is null) return false;
            if (state is not ("pending" or "processing" or "complete" or "failed")) throw new ArgumentException("이미지 태그 상태가 올바르지 않습니다.");
            var updated = item with { ImageTags = tags, ImageTagState = state };
            LinuxPaths.Write(SafePath(Root, Path.Combine(item.RelativeDirectory, "metadata.json")), updated);
            entries = entries.Select(x => x.Id == id ? updated : x).ToArray();
        }
        finally { gate.Release(); }
        Changed?.Invoke(); return true;
    }
}
