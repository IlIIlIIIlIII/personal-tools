using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRT.Interop;
using Windows.Graphics;

namespace PersonalTools;

// A brief acrylic indicator above the taskbar of the active monitor, placed and shaped
// like Windows' volume and brightness indicators. It never takes focus and is absent
// from Alt+Tab and the taskbar. The size comes from the content in DIPs and is turned
// into pixels with the destination monitor's DPI, also when Windows rescales the window
// on arriving at a monitor with another DPI.
internal sealed class WindowSwitchOsd : Window
{
    private const double MinWidthDip = 240, MaxWidthDip = 400, FallbackHeightDip = 60, GapDip = 16;
    private const double TextWidthDip = MaxWidthDip - 12 - 32 - 12 - 16; // padding, leading visual, spacing, padding
    private readonly DispatcherQueueTimer timer;
    private readonly Grid root = new() { Padding = new Thickness(12, 12, 16, 12), ColumnSpacing = 12 };
    private readonly Grid lead = new() { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel heading = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly TextBlock detail = new() { FontSize = 12, LineHeight = 16, MaxWidth = TextWidthDip, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly nint handle;
    private readonly WindowDpiPlacement placement;
    private int shown;
    private bool closed;
    public WindowSwitchOsd()
    {
        Title = "창 빠른 전환";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        detail.Foreground = Brush("TextFillColorSecondaryBrush");
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, MaxWidth = TextWidthDip };
        text.Children.Add(heading); text.Children.Add(detail);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(lead);
        Grid.SetColumn(text, 1); root.Children.Add(text);
        Content = root;
        handle = WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true; presenter.IsResizable = false;
            presenter.IsMinimizable = false; presenter.IsMaximizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, (nint)(style | 0x08000000 | 0x80)); // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        int corners = 2; DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int)); // DWMWCP_ROUND, 8 px like flyouts
        placement = new WindowDpiPlacement(handle, AppWindow, noFrame: true);
        timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (!closed) AppWindow.Hide(); };
        // A pending hide after Close threw on the closed AppWindow and ended the process.
        Closed += (_, _) => { closed = true; timer.Stop(); placement.Dispose(); };
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static Border Keycap(string text) => new()
    {
        Style = (Style)Application.Current.Resources["Keycap"],
        Padding = new Thickness(6, 0, 6, 1), MinWidth = 20, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 12, LineHeight = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }
    };
    // The InfoBar severity icon: a filled circle glyph with the symbol glyph on top.
    private static Grid StatusIcon(double size, string fill, string glyph)
    {
        var icon = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(new FontIcon { Glyph = "\uF136", FontSize = size, Foreground = Brush(fill) });
        icon.Children.Add(new FontIcon { Glyph = glyph, FontSize = size, Foreground = Brush("TextOnAccentFillColorPrimaryBrush") });
        return icon;
    }
    private static Grid Badge(bool success)
    {
        var badge = new Grid { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -5, -5) };
        badge.Children.Add(new Ellipse { Fill = Brush("SolidBackgroundFillColorBaseBrush") });
        badge.Children.Add(success ? StatusIcon(16, "AccentFillColorDefaultBrush", "\uF13E") : StatusIcon(16, "SystemFillColorCautionBrush", "\uF13C"));
        return badge;
    }
    // monitor 0 means the monitor of the foreground window.
    public async void Present(WindowSwitchNotice notice, nint monitor = 0)
    {
        int current = ++shown;
        Image? image = null; FontIcon? fallback = null;
        lead.Children.Clear();
        if (notice.Slot is not null)
        {
            fallback = new FontIcon { Glyph = "\uE737", FontSize = 24, Foreground = Brush("TextFillColorSecondaryBrush") };
            image = new Image { Width = 32, Height = 32 };
            lead.Children.Add(fallback); lead.Children.Add(image); lead.Children.Add(Badge(notice.Success));
        }
        else if (notice.Caution) lead.Children.Add(StatusIcon(20, "SystemFillColorCautionBrush", "\uF13C"));
        else if (notice.Key >= 0)
        {
            lead.Children.Add(new Rectangle { RadiusX = 6, RadiusY = 6, StrokeThickness = 1, StrokeDashArray = [4, 3], Stroke = Brush("ControlStrongStrokeColorDefaultBrush"), Opacity = 0.6 });
            lead.Children.Add(new TextBlock { Text = WindowSlotKeys.Keys[notice.Key].ToString(), FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextFillColorSecondaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
        else lead.Children.Add(StatusIcon(20, "AccentFillColorDefaultBrush", "\uF13F"));

        heading.Children.Clear();
        if (notice.Key >= 0)
        {
            heading.Children.Add(Keycap("Win"));
            heading.Children.Add(Keycap(WindowSlotKeys.Keys[notice.Key].ToString()));
            heading.Children.Add(new TextBlock { Text = notice.KeyText, FontSize = 14, LineHeight = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        else heading.Children.Add(new TextBlock { Text = notice.Title, FontSize = 14, LineHeight = 20, FontWeight = FontWeights.SemiBold, MaxWidth = TextWidthDip, TextTrimming = TextTrimming.CharacterEllipsis });
        // A registered window's title stays on one line; guidance may take two.
        detail.Text = notice.Detail;
        detail.Visibility = notice.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        detail.TextWrapping = notice.Success ? TextWrapping.NoWrap : TextWrapping.Wrap;
        detail.MaxLines = notice.Success ? 1 : 2;

        int dark = Application.Current.RequestedTheme == ApplicationTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // border colour for the theme
        Place(monitor != 0 ? monitor : MonitorFromWindow(GetForegroundWindow(), 2));
        AppWindow.Show(false);
        placement.Restore();
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(notice.Success ? 1500 : 3000);
        timer.Start();
        if (notice.Slot is { } slot && image is not null && fallback is not null)
        {
            var source = await ClipboardAppIcons.GetAsync(slot.AppPath, System.IO.Path.GetFileName(slot.AppPath ?? slot.AppName));
            if (current == shown && source is not null) { image.Source = source; fallback.Visibility = Visibility.Collapsed; }
        }
    }
    private void Place(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        uint dpi = WindowDpiPlacement.DpiOf(monitor);
        double scale = dpi / 96d;
        root.Measure(new Windows.Foundation.Size(MaxWidthDip, double.PositiveInfinity));
        var size = root.DesiredSize;
        double widthDip = Math.Clamp(Math.Ceiling(size.Width), MinWidthDip, MaxWidthDip);
        double heightDip = size.Height > 0 ? Math.Ceiling(size.Height) : FallbackHeightDip;
        int workWidth = info.Work.Right - info.Work.Left, workHeight = info.Work.Bottom - info.Work.Top;
        int width = Math.Min(workWidth, (int)Math.Ceiling(widthDip * scale));
        int height = Math.Min(workHeight, (int)Math.Ceiling(heightDip * scale));
        int left = info.Work.Left + (workWidth - width) / 2;
        int top = Math.Max(info.Work.Top, info.Work.Bottom - height - (int)Math.Round(GapDip * scale));
        // Moving a visible window across monitors would show it at the old size for a frame.
        if (AppWindow.IsVisible && GetDpiForWindow(handle) != dpi) AppWindow.Hide();
        placement.MoveAndResize(new RectInt32(left, top, width, height));
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
