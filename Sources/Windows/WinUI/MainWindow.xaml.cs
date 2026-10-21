using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace PersonalTools;
public sealed partial class MainWindow : Window
{
    private readonly ClipboardController clipboard;
    private readonly AudioController audio;
    private readonly WindowSwitchController windowSwitch;
    private ClipboardPopup? clipboardPopup;
    private Task? clipboardStartup;
    private TrayIcon? tray;
    private bool quitting, started;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Personal Tools";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "PersonalTools.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)(1120 * dpi), area.Width - 40), Math.Min((int)(820 * dpi), area.Height - 40)));

        clipboard = new ClipboardController(((App)Application.Current).Bridge, DispatcherQueue);
        audio = new AudioController(DispatcherQueue);
        windowSwitch = new WindowSwitchController(DispatcherQueue);

        ClipsPage.Configure(clipboard);
        ClipsPage.PasteRequested = async item => await clipboard.CopyAsync(item);
        ClipsPage.SettingsRequested += ShowClipboardSettings;
        ClipSettingsPage.Configure(clipboard, this);
        ClipSettingsPage.BackRequested += () => ShowPage("clipboard");
        ClipSettingsPage.BridgeRequested += () => Navigation.SelectedItem = BridgeNav;
        clipboard.TogglePopup += () =>
        {
            try
            {
                clipboardPopup ??= new ClipboardPopup(clipboard, () => DispatcherQueue.TryEnqueue(Show), () => DispatcherQueue.TryEnqueue(() => { Show(); ShowClipboardSettings(); }));
                clipboardPopup.Toggle();
            }
            catch { /* The panel reports its own errors; the main window stays usable. */ }
        };
        SwitchPage.Configure(windowSwitch);
        SoundsPage.Configure(audio, this);
        BridgePage.Configure(this);
        DiagPage.Configure(audio, clipboard, windowSwitch, BridgePage);
        Navigation.SelectedItem = ClipboardNav;

        try { tray = new TrayIcon(WindowNative.GetWindowHandle(this), () => DispatcherQueue.TryEnqueue(Show), () => DispatcherQueue.TryEnqueue(Quit)); }
        catch { tray = null; }
        AppWindow.Closing += (_, e) =>
        {
            if (!quitting && tray?.Available == true) { e.Cancel = true; AppWindow.Hide(); }
            else if (!quitting) { e.Cancel = true; Quit(); }
        };
        Closed += (_, _) => { tray?.Dispose(); audio.Dispose(); windowSwitch.Dispose(); };
    }
    public void Show() { AppWindow.Show(); Activate(); }
    public void Start(bool inTray)
    {
        if (started) return;
        started = true;
        clipboardStartup = clipboard.StartAsync(WindowNative.GetWindowHandle(this));
        audio.Start();
        windowSwitch.Start();
        if (!inTray || tray?.Available != true) Show();
        // Start even when the window stays hidden in the tray; the page does not need to load.
        _ = BridgePage.StartBridgeAsync();
    }
    private void ShowClipboardSettings()
    {
        Navigation.SelectedItem = ClipboardNav;
        ShowPage("clipboard-settings");
    }
    private void ShowPage(string tag)
    {
        ClipsPage.Visibility = tag == "clipboard" ? Visibility.Visible : Visibility.Collapsed;
        ClipSettingsPage.Visibility = tag == "clipboard-settings" ? Visibility.Visible : Visibility.Collapsed;
        SwitchPage.Visibility = tag == "windows" ? Visibility.Visible : Visibility.Collapsed;
        SoundsPage.Visibility = tag == "sound" ? Visibility.Visible : Visibility.Collapsed;
        BridgePage.Visibility = tag == "antigravity" ? Visibility.Visible : Visibility.Collapsed;
        DiagPage.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "clipboard-settings") ClipSettingsPage.Load();
        if (tag == "diagnostics") DiagPage.Shown();
    }
    private void NavigationDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        sender.IsPaneToggleButtonVisible = args.DisplayMode != NavigationViewDisplayMode.Expanded;
    }
    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if ((args.SelectedItem as NavigationViewItem)?.Tag is not string tag) return;
        ShowPage(tag);
    }
    // The exit item is not selectable, so it is handled on invoke.
    private void NavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if ((args.InvokedItemContainer as NavigationViewItem)?.Tag is "exit") Quit();
    }
    private async void Quit()
    {
        if (quitting) return;
        quitting = true;
        try
        {
            if (clipboardStartup is not null) await clipboardStartup;
            await ClipsPage.ShutdownAsync();
            if (clipboardPopup is not null) await clipboardPopup.ShutdownAsync();
            await clipboard.DisposeAsync();
            windowSwitch.Dispose();
            await BridgePage.ShutdownAsync();
        }
        finally { Close(); }
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
