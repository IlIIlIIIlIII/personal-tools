using Tmds.DBus;
namespace PersonalTools;
[DBusInterface("io.griplabs.PersonalTools.Shell1")]
public interface IPersonalToolsShell : IDBusObject
{
    Task ShowClipboardAsync(); Task<string> PutClipboardAsync(string entryId, bool paste);
    Task<string> GetStatusAsync(); Task<string> ConfigureShortcutsAsync(string json);
}
internal sealed class Subscription(Action stop) : IDisposable { public void Dispose() => stop(); }
