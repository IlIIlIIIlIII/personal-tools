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

// A brief acrylic notice at the bottom centre of the active monitor, like the
// volume flyout. It never takes focus and is absent from Alt+Tab and the taskbar.
internal sealed class WindowSwitchOsd : Window
{
    private const int WidthDip = 400, HeightDip = 68;
    private readonly DispatcherQueueTimer timer;
    private readonly StackPanel heading = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock detail = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly Image icon = new() { Width = 36, Height = 36 };
    private readonly Grid placeholder = new() { Width = 36, Height = 36 };
    private readonly nint handle;
    private int shown;
    public WindowSwitchOsd()
    {
        Title = "창 빠른 전환";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        detail.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        placeholder.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, StrokeThickness = 1, StrokeDashArray = [4, 3],
            Stroke = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"] });
        placeholder.Children.Add(new FontIcon { Glyph = "", FontSize = 18, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(heading); text.Children.Add(detail);
        var root = new Grid { Padding = new Thickness(14, 12, 18, 12), ColumnSpacing = 14 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(icon); root.Children.Add(placeholder);
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
        int corners = 2; DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => AppWindow.Hide();
    }
    private static Border Keycap(string text) => new()
    {
        Padding = new Thickness(6, 1, 6, 2), CornerRadius = new CornerRadius(4), MinWidth = 22,
        Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] }
    };
    public async void Present(WindowSwitchNotice notice)
    {
        int current = ++shown;
        heading.Children.Clear();
        if (notice.Key >= 0)
        {
            heading.Children.Add(Keycap("Win"));
            heading.Children.Add(Keycap(WindowSlotKeys.Keys[notice.Key].ToString()));
            heading.Children.Add(new TextBlock { Text = notice.KeyText, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        else heading.Children.Add(new TextBlock { Text = notice.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        detail.Text = notice.Detail;
        icon.Source = null;
        icon.Visibility = notice.Slot is null ? Visibility.Collapsed : Visibility.Visible;
        placeholder.Visibility = notice.Slot is null ? Visibility.Visible : Visibility.Collapsed;
        PlaceOnActiveMonitor();
        AppWindow.Show(false);
        timer.Stop(); timer.Start();
        if (notice.Slot is { } slot)
        {
            var image = await ClipboardAppIcons.GetAsync(slot.AppPath, System.IO.Path.GetFileName(slot.AppPath ?? slot.AppName));
            if (current == shown) icon.Source = image;
        }
    }
    private void PlaceOnActiveMonitor()
    {
        var monitor = MonitorFromWindow(GetForegroundWindow(), 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96d : 1;
        int width = (int)Math.Round(WidthDip * scale), height = (int)Math.Round(HeightDip * scale);
        int x = info.Work.Left + (info.Work.Right - info.Work.Left - width) / 2;
        int y = info.Work.Bottom - height - (int)Math.Round(24 * scale);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
