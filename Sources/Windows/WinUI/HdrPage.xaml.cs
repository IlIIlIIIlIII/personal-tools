using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PersonalTools;

// Auto-HDR page (design canvas https://claude.ai/artifact/6HEp13fuWcEHtSj3sYAcNK).
public sealed partial class HdrPage : UserControl
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private HdrController? controller;
    private Window? owner;
    private HdrSnapshot snapshot = new([], [], null);
    private string? rendered;
    private bool updating;
    public HdrPage() { InitializeComponent(); }
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    internal void Configure(HdrController service, Window window)
    {
        controller = service; owner = window;
        service.Changed += value => DispatcherQueue.TryEnqueue(() => Render(value));
        Render(service.Snapshot);
    }
    private void ShowError(Exception e) { ActionError.Message = e.Message; ActionError.Visibility = Visibility.Visible; ActionError.IsOpen = true; }
    private void ActionErrorClosed(InfoBar sender, InfoBarClosedEventArgs args) => sender.Visibility = Visibility.Collapsed;

    private void Render(HdrSnapshot value)
    {
        string serialized = JsonSerializer.Serialize(value);
        if (rendered == serialized) return;
        rendered = serialized;
        snapshot = value;
        // The controller fills Active from its first refresh on; before that nothing is known yet.
        bool ready = value.Active is not null;
        var active = value.Active ?? [];
        var primary = value.Monitors.FirstOrDefault(m => m.Primary);
        string running = string.Join(", ", value.Programs.Where(p => p.Running && p.Program.Enabled).Select(p => p.Program.Name));
        updating = true;

        string owners = string.Join(", ", active.SelectMany(a => a.Programs).Distinct());
        bool hdrOn = value.Monitors.Any(m => m.Enabled && active.Any(a => a.MonitorId == m.Id));
        var (tone, text) = !ready ? ("", "확인 중")
            : value.Error is not null ? ("warn", "확인 필요")
            : !value.Enabled ? ("", "꺼짐")
            : active.Length > 0 ? hdrOn ? ("ok", "HDR 켬 · " + owners) : ("", owners + " 실행 중")
            : value.Programs.Length == 0 ? ("", "프로그램 없음")
            : ("", "대기 중");
        StateDot.Fill = Resource(tone switch { "ok" => "SystemFillColorSuccessBrush", "warn" => "SystemFillColorCautionBrush", _ => "TextFillColorTertiaryBrush" });
        StateText.Text = text;

        string? problem = value.Error ?? value.Warning;
        Status.IsOpen = problem is not null;
        Status.Visibility = problem is not null ? Visibility.Visible : Visibility.Collapsed;
        Status.Severity = value.Error is not null ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        Status.Title = value.Error is not null && (primary is null || !primary.Supported || primary.Error is not null) ? "HDR을 켜지 못했습니다" : "";
        Status.Message = problem ?? "";

        EnabledSwitch.IsOn = value.Enabled;
        EnabledSwitch.IsEnabled = ready;

        if (primary is null)
        {
            MonitorTitle.Text = ready ? "주 모니터를 찾지 못했습니다" : "주 모니터 확인 중";
            MonitorDetail.Text = "";
            MonitorBadge.Visibility = Visibility.Collapsed;
            MonitorSwitch.IsOn = false;
            MonitorSwitch.IsEnabled = false;
        }
        else
        {
            bool usable = primary.Supported && primary.Error is null;
            MonitorTitle.Text = primary.Name + " · 주 모니터";
            MonitorDetail.Text = Describe(primary, active.FirstOrDefault(a => a.MonitorId == primary.Id), running);
            Badge(MonitorBadge, MonitorBadgeText, primary.Error is not null ? "HDR 제한" : primary.Supported ? "HDR 지원" : "HDR 미지원", usable ? "" : "warn");
            MonitorSwitch.IsOn = primary.Enabled;
            MonitorSwitch.IsEnabled = usable;
        }

        Programs.Children.Clear();
        foreach (var item in value.Programs) Programs.Children.Add(ProgramRow(item));
        AddButton.Visibility = value.Programs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = ready && value.Programs.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        updating = false;
    }

    private static string Describe(HdrMonitor monitor, HdrActive? session, string running)
    {
        if (monitor.Error is not null) return monitor.Error;
        if (!monitor.Supported)
            return string.Join(" · ", new[] { monitor.Internal ? "노트북 내장 화면" : null, running.Length > 0 ? running + " 실행 중" : "HDR을 지원하지 않는 모니터입니다" }.OfType<string>());
        if (session is null) return "프로그램이 시작될 때의 주 모니터에만 적용합니다. 다른 모니터는 그대로 둡니다.";
        string names = string.Join(", ", session.Programs);
        if (session.Manual) return $"{names} 실행 중 · 직접 바꾼 설정을 유지하고, 종료해도 되돌리지 않습니다.";
        if (session.Original) return $"{names} 실행 중 · 원래 켜져 있었으므로 종료 뒤에도 그대로 둡니다.";
        string time = session.StartedAt is { } at ? at.ToLocalTime().ToString("HH:mm") + " " : "";
        return $"{time}{names} 실행으로 켰습니다. 종료하면 다시 끕니다.";
    }

    private static void Badge(Border border, TextBlock label, string text, string tone)
    {
        border.Visibility = Visibility.Visible;
        border.Background = Resource(tone switch { "ok" => "SystemFillColorSuccessBackgroundBrush", "warn" => "SystemFillColorCautionBackgroundBrush", _ => "SubtleFillColorSecondaryBrush" });
        label.Foreground = Resource(tone switch { "ok" => "SystemFillColorSuccessBrush", "warn" => "SystemFillColorCautionBrush", _ => "TextFillColorSecondaryBrush" });
        label.Text = text;
    }
    private static Border Badge(string text, string tone)
    {
        var label = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        var border = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center, Child = label };
        Badge(border, label, text, tone);
        return border;
    }

    // Shows the executable's own icon once the shell hands it over; a game glyph until then.
    private static FrameworkElement AppIcon(string path, double size)
    {
        var host = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var fallback = new Border
        {
            CornerRadius = new CornerRadius(size >= 32 ? 6 : 5), Background = Resource("SubtleFillColorSecondaryBrush"),
            Child = new FontIcon { Glyph = "", FontSize = size / 2, Foreground = Resource("TextFillColorSecondaryBrush") },
        };
        host.Children.Add(fallback);
        _ = LoadIconAsync(host, fallback, path);
        return host;
    }
    private static async Task LoadIconAsync(Grid host, Border fallback, string path)
    {
        var image = await ClipboardAppIcons.GetAsync(path, Path.GetFileName(path));
        if (image is null) return;
        host.Children.Remove(fallback);
        host.Children.Add(new Image { Source = image, Width = host.Width, Height = host.Height });
    }

    private Border ProgramRow(HdrProgramStatus item)
    {
        var program = item.Program;
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(AppIcon(program.Path, 32));

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = program.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = program.Path, Style = StyleOf("CardDescription"), FontFamily = Mono, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTipService.SetToolTip(text, program.Path);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        if (item.Running) actions.Children.Add(Badge("실행 중", "ok"));
        else if (program.Enabled) actions.Children.Add(new TextBlock { Text = "대기", FontSize = 13, Foreground = Resource("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        var toggle = new ToggleSwitch { IsOn = program.Enabled, OnContent = "켬", OffContent = "끔", MinWidth = 0 };
        AutomationProperties.SetName(toggle, program.Name + " 자동 적용");
        toggle.Toggled += async (_, _) =>
        {
            bool value = toggle.IsOn;
            try { await Task.Run(() => controller!.SaveProgramAsync(program with { Enabled = value })); }
            catch (Exception e) { ShowError(e); }
        };
        var more = new Button { Style = StyleOf("SubtleIconButton"), Width = 32, Height = 32, Content = new FontIcon { Glyph = "", FontSize = 16 } };
        AutomationProperties.SetName(more, program.Name + " 편집 또는 삭제");
        var edit = new MenuFlyoutItem { Text = "편집", Icon = new FontIcon { Glyph = "" } };
        edit.Click += async (_, _) => await EditAsync(program, "프로그램 편집", "저장");
        var delete = new MenuFlyoutItem { Text = "삭제", Icon = new FontIcon { Glyph = "" } };
        delete.Click += async (_, _) =>
        {
            try { await Task.Run(() => controller!.SaveProgramAsync(program, true)); }
            catch (Exception e) { ShowError(e); }
        };
        var menu = new MenuFlyout();
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        more.Flyout = menu;
        actions.Children.Add(toggle);
        actions.Children.Add(more);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return new Border { Style = StyleOf("SettingCard"), Child = grid };
    }

    private static Grid CandidateRow(HdrCandidate candidate, bool registered)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(AppIcon(candidate.Path, 24));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = candidate.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = candidate.Path, FontFamily = Mono, FontSize = 12, Foreground = Resource("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (registered || candidate.Launcher)
        {
            var badge = Badge(registered ? "등록됨" : "런처", registered ? "" : "warn");
            if (!registered) ToolTipService.SetToolTip(badge, "게임 런처입니다. 실제 게임 실행 파일을 고르세요.");
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }
        return grid;
    }

    private async void Add(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidates = await Task.Run(HdrController.ListCandidates);
            var registered = snapshot.Programs.Select(p => p.Program.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var search = new AutoSuggestBox { PlaceholderText = "실행 중인 프로그램 검색", QueryIcon = new SymbolIcon(Symbol.Find) };
            AutomationProperties.SetName(search, "실행 중인 프로그램 검색");
            var list = new ListView
            {
                Height = 272, SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0, 4, 0, 4),
                Background = Resource("CardBackgroundFillColorSecondaryBrush"), BorderBrush = Resource("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            };
            AutomationProperties.SetName(list, "실행 중인 프로그램");
            var none = new TextBlock { Text = "창이 열린 프로그램이 없습니다. 파일 찾아보기로 추가하세요.", Style = StyleOf("Muted"), Visibility = Visibility.Collapsed };
            var name = new TextBox { Header = "표시 이름" };
            void Fill()
            {
                string query = search.Text.Trim();
                list.Items.Clear();
                foreach (var candidate in candidates.Where(c => query.Length == 0 || c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || c.Path.Contains(query, StringComparison.OrdinalIgnoreCase)))
                {
                    bool taken = registered.Contains(candidate.Path);
                    list.Items.Add(new ListViewItem { Content = CandidateRow(candidate, taken), Tag = candidate, IsEnabled = !taken, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                none.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            Fill();
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(new TextBlock { Text = "실행 중인 프로그램에서 고르세요. 목록에 없으면 실행 파일을 직접 찾습니다.", TextWrapping = TextWrapping.Wrap, Foreground = Resource("TextFillColorSecondaryBrush") });
            panel.Children.Add(search);
            panel.Children.Add(list);
            panel.Children.Add(none);
            panel.Children.Add(name);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "프로그램 추가", Content = panel, PrimaryButtonText = "추가", SecondaryButtonText = "파일 찾아보기…",
                CloseButtonText = "취소", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false,
            };
            void Validate() => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem && !string.IsNullOrWhiteSpace(name.Text);
            search.TextChanged += (_, _) => { Fill(); Validate(); };
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is ListViewItem { Tag: HdrCandidate picked }) name.Text = picked.Name;
                Validate();
            };
            name.TextChanged += (_, _) => Validate();
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && list.SelectedItem is ListViewItem { Tag: HdrCandidate chosen })
            {
                string label = name.Text;
                await Task.Run(() => controller!.SaveProgramAsync(new(Guid.NewGuid(), label, chosen.Path)));
            }
            else if (result == ContentDialogResult.Secondary) await BrowseAsync();
        }
        catch (Exception error) { ShowError(error); }
    }
    private async Task BrowseAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner!));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        string label = await Task.Run(() => HdrController.DisplayName(file.Path));
        await EditAsync(new(Guid.NewGuid(), label, file.Path), "프로그램 추가", "추가");
    }
    private async Task EditAsync(HdrProgram program, string title, string action)
    {
        try
        {
            var name = new TextBox { Header = "표시 이름", Text = program.Name };
            var path = new TextBox { Header = "실행 파일 전체 경로", Text = program.Path, FontFamily = Mono };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(name);
            panel.Children.Add(path);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = panel, PrimaryButtonText = action, CloseButtonText = "취소", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var edited = program with { Name = name.Text, Path = path.Text.Trim().Trim('"') };
                await Task.Run(() => controller!.SaveProgramAsync(edited));
            }
        }
        catch (Exception e) { ShowError(e); }
    }

    private async void EnabledToggled(object sender, RoutedEventArgs e)
    {
        if (updating || controller is null) return;
        bool value = EnabledSwitch.IsOn;
        EnabledSwitch.IsEnabled = false;
        try { await Task.Run(() => controller.SetEnabledAsync(value)); }
        catch (Exception error) { ShowError(error); }
        finally { rendered = null; Render(controller.Snapshot); }
    }
    private async void MonitorToggled(object sender, RoutedEventArgs e)
    {
        if (updating || controller is null || snapshot.Monitors.FirstOrDefault(m => m.Primary) is not { } primary) return;
        bool value = MonitorSwitch.IsOn;
        MonitorSwitch.IsEnabled = false;
        try { await Task.Run(() => controller.RequestAsync(new("set", primary.Id, value))); }
        catch (Exception error) { ShowError(error); }
        finally { rendered = null; Render(controller.Snapshot); }
    }
}
