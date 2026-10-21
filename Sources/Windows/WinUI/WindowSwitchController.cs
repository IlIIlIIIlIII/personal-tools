using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace PersonalTools;

public sealed record WindowSwitchSettings
{
    public bool Enabled { get; init; } = true;
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalTools", "WindowSwitch", "settings.json");
    public static WindowSwitchSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<WindowSwitchSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}

// UI-thread facade. Registrations live for this app session and survive turning
// the shortcuts off and on again; the hook exists only while enabled.
internal sealed class WindowSwitchController : IDisposable
{
    private readonly DispatcherQueue dispatcher;
    private WindowShortcuts? shortcuts;
    private WindowSwitchOsd? osd;
    public WindowSlotTable Slots { get; } = new();
    public WindowSwitchSettings Settings { get; private set; } = WindowSwitchSettings.Load();
    public string? Error { get; private set; }
    public bool Running => shortcuts is not null;
    public event Action? Changed;
    public WindowSwitchController(DispatcherQueue dispatcher) => this.dispatcher = dispatcher;
    public void Start() { if (Settings.Enabled) Install(); }
    private void Install()
    {
        if (shortcuts is not null) return;
        try
        {
            var created = new WindowShortcuts(Slots);
            created.Changed += () => dispatcher.TryEnqueue(() => Changed?.Invoke());
            created.Notice += notice => dispatcher.TryEnqueue(() =>
            {
                try { (osd ??= new WindowSwitchOsd()).Present(notice); } catch { /* The switch itself already happened. */ }
            });
            shortcuts = created; Error = null;
        }
        catch (Exception error) { Error = error.Message; }
        Changed?.Invoke();
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled == Settings.Enabled && enabled == Running) return;
        Settings = Settings with { Enabled = enabled };
        try { Settings.Save(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Error = "설정을 저장하지 못했습니다: " + error.Message; }
        if (enabled) Install();
        else { shortcuts?.Dispose(); shortcuts = null; Changed?.Invoke(); }
    }
    public void Unregister(int slot) { if (Slots.Clear(slot)) Changed?.Invoke(); }
    public void Dispose()
    {
        shortcuts?.Dispose(); shortcuts = null;
        osd?.Close(); osd = null;
    }
}
