using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace PersonalTools;

// Replaces the generated Main (DISABLE_XAML_GENERATED_MAIN). The logon task also runs the app every
// minute, so it comes back when something ends it from outside (2026-10-26: gone overnight, the
// panels left dark); such a --startup run leaves before WinUI loads when there is nothing to do.
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--startup", StringComparer.OrdinalIgnoreCase) && !ShouldStartFromTask()) return;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }

    // Not when it is running, after 앱 종료 in this sign-in, or before the taskbar is up (a later run starts it).
    private static bool ShouldStartFromTask()
    {
        if (Mutex.TryOpenExisting(App.InstanceName, out var running)) { running.Dispose(); return false; }
        return !QuitMarker.IsSet && FindWindow("Shell_TrayWnd", null) != 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? windowName);
}

// 앱 종료 marks the current sign-in so the task's runs leave the app closed. The key is volatile:
// signing out or restarting Windows removes it, and the next sign-in starts the app as usual.
internal static class QuitMarker
{
    private const string Parent = @"Software\PersonalTools", Name = "Quit";
    public static bool IsSet
    {
        get { using var key = Registry.CurrentUser.OpenSubKey($@"{Parent}\{Name}"); return key is not null; }
    }
    public static void Set()
    {
        // The parent is created first so it stays a normal key; only the marker is volatile.
        try { using var parent = Registry.CurrentUser.CreateSubKey(Parent); parent.CreateSubKey(Name, true, RegistryOptions.Volatile).Dispose(); }
        catch { /* Without the marker the task only brings the app back within a minute. */ }
    }
    public static void Clear()
    {
        try { Registry.CurrentUser.DeleteSubKey($@"{Parent}\{Name}", false); }
        catch { }
    }
}
