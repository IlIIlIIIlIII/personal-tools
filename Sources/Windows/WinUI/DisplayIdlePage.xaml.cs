using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PersonalTools;

// 화면 자동 끄기 page (design canvas https://claude.ai/artifact/7dtoP5r9f84XYigQQUiJQE).
public sealed partial class DisplayIdlePage : UserControl
{
    private static readonly int[] Waits = [30, 60, 120, 300, 600, 1800];
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DisplayIdleController? controller;
    private DisplayIdleSnapshot snapshot = new(new(), [], []);
    private string? rendered;
    private bool updating;
    public DisplayIdlePage()
    {
        InitializeComponent();
        clock.Tick += (_, _) => { if (XamlRoot is not { IsHostVisible: false }) RenderState(); };
    }
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    internal void Configure(DisplayIdleController service)
    {
        controller = service;
        service.Changed += value => DispatcherQueue.TryEnqueue(() => Render(value));
        Render(service.Snapshot);
    }
    // MainWindow calls these as the page comes and goes; DDC/CI is asked only when the page opens.
    public void Shown() { controller?.Refresh(); clock.Start(); RenderState(); }
    public void Hidden() => clock.Stop();

    private void Render(DisplayIdleSnapshot value)
    {
        string key = value.RenderKey;
        if (rendered == key) return;
        rendered = key;
        snapshot = value;
        bool ready = value.Monitors.Length > 0;
        var settings = value.Settings;
        updating = true;

        EnabledSwitch.IsOn = settings.Enabled;
        EnabledSwitch.IsEnabled = ready;
        RenderWait((int)(settings.IdleMilliseconds / 1000));
        WaitBox.IsEnabled = ready;
        DarkenButton.IsEnabled = ready && settings.Enabled && !value.Dark && value.Monitors.Any(m => m.Selected);

        var silent = value.Monitors.Where(m => m.Selected && m.Responding == false).Select(m => m.Name).ToArray();
        Show(Unresponsive, silent.Length > 0);
        if (silent.Length > 0) Unresponsive.Title = $"{string.Join(", ", silent)}{DisplayIdleController.Subject(silent[^1])} 응답하지 않습니다.";
        Show(ErrorBar, value.Error is not null);
        ErrorBar.Message = value.Error ?? "";

        MonitorList.Children.Clear();
        foreach (var monitor in value.Monitors) MonitorList.Children.Add(MonitorRow(monitor));
        MonitorsPending.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;

        RecentList.Children.Clear();
        for (int i = 0; i < value.Recent.Length; i++) RecentList.Children.Add(RecentRow(value.Recent[i], i > 0));
        RecentCard.Visibility = value.Recent.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentEmpty.Visibility = value.Recent.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        updating = false;
        RenderState();
    }
    private static void Show(InfoBar bar, bool open) { bar.IsOpen = open; bar.Visibility = open ? Visibility.Visible : Visibility.Collapsed; }

    // The pill counts down while it waits, so it is redrawn every second while the page is open.
    private void RenderState()
    {
        var value = snapshot;
        string tone = "", text;
        bool moon = false;
        if (value.Monitors.Length == 0) text = "확인 중";
        else if (!value.Settings.Enabled) text = "사용 안 함";
        else if (value.Dark && value.DarkCount > 0)
        {
            moon = true;
            var minutes = (int)(DateTime.Now - (value.DarkSince ?? DateTime.Now)).TotalMinutes;
            text = $"{value.DarkCount}대 꺼짐 · " + (minutes < 1 ? "방금" : minutes < 60 ? $"{minutes}분째" : minutes % 60 == 0 ? $"{minutes / 60}시간째" : $"{minutes / 60}시간 {minutes % 60}분째");
        }
        else if (!value.Monitors.Any(m => m.Selected)) text = "끌 모니터 없음";
        else if (value.Dark) text = "끌 수 있는 모니터 없음";
        else
        {
            tone = "ok";
            long left = value.Settings.IdleMilliseconds - (Environment.TickCount64 - (controller?.LastInput ?? 0));
            text = left <= 1000 ? "켜짐 · 곧 끔" : $"켜짐 · {DisplayIdleController.Duration((left + 999) / 1000)} 뒤 끔";
        }
        StateMoon.Visibility = moon ? Visibility.Visible : Visibility.Collapsed;
        StateDot.Visibility = moon ? Visibility.Collapsed : Visibility.Visible;
        StateDot.Fill = Resource(tone == "ok" ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush");
        StateText.Text = text;
    }

    // Preset times, plus the saved one when settings.json holds another value.
    private void RenderWait(int seconds)
    {
        var values = Waits.Contains(seconds) ? Waits : [.. Waits.Append(seconds).Order()];
        if (WaitBox.Items.Count != values.Length || !WaitBox.Items.Cast<ComboBoxItem>().Select(i => (int)i.Tag).SequenceEqual(values))
        {
            WaitBox.Items.Clear();
            foreach (int value in values) WaitBox.Items.Add(new ComboBoxItem { Content = DisplayIdleController.Duration(value), Tag = value });
        }
        WaitBox.SelectedIndex = Array.IndexOf(values, seconds);
    }

    private Border MonitorRow(DisplayIdleMonitor monitor)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var secondary = Resource("TextFillColorSecondaryBrush");
        var icon = new FontIcon { Glyph = monitor.Supported ? "" : "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        if (!monitor.Supported) icon.Foreground = secondary;
        grid.Children.Add(icon);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = monitor.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        if (!monitor.Supported) name.Foreground = secondary;
        text.Children.Add(name);
        string detail = monitor.Detail + (monitor.Supported && !monitor.Selected && monitor.Responding == false ? " · DDC/CI 응답 없음" : "");
        text.Children.Add(new TextBlock { Text = detail, Style = StyleOf("CardDescription") });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        if (monitor.Dark) actions.Children.Add(HdrPage.Badge("꺼짐", ""));
        else if (monitor.Selected && monitor.Responding == false) actions.Children.Add(HdrPage.Badge("응답 없음", "warn"));
        var toggle = new ToggleSwitch { IsOn = monitor.Selected, IsEnabled = monitor.Supported, OnContent = "켬", OffContent = "끔", MinWidth = 0 };
        AutomationProperties.SetName(toggle, monitor.Name + " 자동으로 끄기");
        toggle.Toggled += (_, _) => { if (!updating) controller?.SetMonitor(monitor.Id, toggle.IsOn); };
        actions.Children.Add(toggle);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return new Border { Style = StyleOf("SettingCard"), Child = grid };
    }

    private static Grid RecentRow(DisplayIdleEvent item, bool divider)
    {
        var secondary = Resource("TextFillColorSecondaryBrush");
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 44 };
        if (divider) { grid.BorderBrush = Resource("DividerStrokeColorDefaultBrush"); grid.BorderThickness = new Thickness(0, 1, 0, 0); }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string time = item.At.Date == DateTime.Today ? item.At.ToString("HH:mm:ss") : item.At.ToString("M/d HH:mm");
        grid.Children.Add(new TextBlock { Text = time, FontSize = 13, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = item.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var note = new TextBlock { Text = item.Note ?? "", FontSize = 13, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(note, 2);
        grid.Children.Add(note);
        return grid;
    }

    private void EnabledToggled(object sender, RoutedEventArgs e)
    {
        if (!updating) controller?.SetEnabled(EnabledSwitch.IsOn);
    }
    private void WaitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && WaitBox.SelectedItem is ComboBoxItem { Tag: int seconds }) controller?.SetIdleSeconds(seconds);
    }
    private void DarkenClick(object sender, RoutedEventArgs e) => controller?.DarkenNow();
}
