using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace PersonalTools;

public sealed record ClipFileRow(string Name, string Size);
public sealed class ClipGroup(string key, IEnumerable<ClipboardCard> cards) : List<ClipboardCard>(cards)
{
    public string Key { get; } = key;
}
public sealed class ClipboardCard(ClipEntry entry, string format) : INotifyPropertyChanged
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    // Text copied from these apps is usually code, so the quick panel shows it monospaced.
    private static readonly HashSet<string> CodeApps = new(StringComparer.OrdinalIgnoreCase)
        { "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "Code", "Cursor", "devenv", "rider64", "idea64", "wezterm-gui", "alacritty" };
    private bool current, hovered;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ClipEntry Entry { get; } = entry;
    public string Format { get; } = format;
    public Brush? TypeBrush { get; init; }
    public string Badge { get; init; } = "";
    public Brush? BadgeBackground { get; init; }
    public Brush? BadgeForeground { get; init; }
    public int PreviewLines { get; init; } = 4;
    public override string ToString() => Description;
    public string FormatLabel => Format switch { "Url" => "URL", "Image" => "이미지", "Files" => $"파일 {Entry.Attachments.Length}개", _ => "텍스트" };
    public string FormatIcon => Format switch { "Url" => "", "Image" => "", "Files" => "", _ => "" };
    public string AppLabel => Path.GetFileNameWithoutExtension(Entry.SourceApp);
    public string TimeLabel
    {
        get
        {
            var local = Entry.CreatedAt.ToLocalTime();
            var today = DateTime.Today;
            return local.Date == today ? local.ToString("HH:mm", Korean)
                : local.Date == today.AddDays(-1) ? "어제 " + local.ToString("HH:mm", Korean)
                : local.Year == today.Year ? local.ToString("M월 d일", Korean) : local.ToString("yyyy. M. d.", Korean);
        }
    }
    public string Footer => $"{AppLabel} · {TimeLabel}";
    public string Description => $"{(Entry.Pinned ? "고정됨, " : "")}{FormatLabel}, {Entry.Preview}, {AppLabel}, {TimeLabel}";
    public string UrlHost => Uri.TryCreate(Entry.Preview.Trim(), UriKind.Absolute, out var uri) ? uri.Host : Entry.Preview;
    public ClipFileRow[] Files => Entry.Attachments.Take(3).Select(x => new ClipFileRow(x.OriginalName, Size(x.Length)))
        .Concat(Entry.Attachments.Length > 3 ? new[] { new ClipFileRow($"외 {Entry.Attachments.Length - 3}개", "") } : Array.Empty<ClipFileRow>()).ToArray();
    private static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576d:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";
    public Visibility TextVisibility => Format == "Text" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UrlVisibility => Format == "Url" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageVisibility => Format == "Image" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FilesVisibility => Format == "Files" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PinVisibility => Entry.Pinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TagVisibility => Entry.ImageTags is not null && Badge.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    // Quick panel card. Media sits 4 px inside the card edge so its 4 px corners stay
    // concentric with the card's 8 px ones; the footer keeps the 12 px text inset.
    private bool Media => Format == "Image";
    private bool Code => Format == "Text" && CodeApps.Contains(AppLabel);
    public Thickness CardPadding => Media ? new Thickness(3, 3, 3, 11) : new Thickness(11);
    public Thickness FooterPadding => Media ? new Thickness(8, 0, 8, 0) : new Thickness(0);
    public Thickness TextMargin => Entry.Pinned ? new Thickness(0, 0, 20, 0) : new Thickness(0);
    public FontFamily PreviewFont => Code ? new FontFamily("Cascadia Mono, Consolas") : (FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"];
    public double PreviewFontSize => Code ? 12 : 14;
    public double PreviewLineHeight => Code ? 18 : 20;
    public int PanelLines => (Code ? 10 : 9) - (Badge.Length > 0 ? 2 : 0);
    public int UrlLines => Badge.Length > 0 ? 3 : 5;
    public string TotalSize => Size(Entry.Attachments.Sum(x => x.Length));
    public string PinActionGlyph => Entry.Pinned ? "" : "";
    public string PinActionLabel => Entry.Pinned ? "고정 해제" : "고정";
    // The selected card shows the Enter hint; a hovered card shows its actions instead.
    public bool IsCurrent { get => current; set { if (current == value) return; current = value; Changed(nameof(HintVisibility)); } }
    public bool IsHovered
    {
        get => hovered;
        set { if (hovered == value) return; hovered = value; Changed(nameof(HintVisibility)); Changed(nameof(ActionsVisibility)); }
    }
    public Visibility HintVisibility => current && !hovered ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionsVisibility => hovered ? Visibility.Visible : Visibility.Collapsed;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum ClipboardPageMode { Main, Panel }

public sealed partial class ClipboardPage : UserControl
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private ClipboardController? controller;
    private CancellationTokenSource? search, filter;
    private Task? searchTask;
    private string[]? resultIds;
    private IReadOnlyDictionary<string, string>? resultEvidence;
    private HashSet<string>? searchScope;
    private bool closed, active = true, updatingTabs, aiMode, pendingCardFocus, restoreCardFocus;
    private int searchRevision;
    private ClipboardFilter filters = new();
    private string selectedCategory = "";
    private string categorySignature = "";
    private ContentDialog? currentDialog;
    private readonly HashSet<FlyoutBase> openFlyouts = [];
    private readonly Windows.UI.ViewManagement.UISettings appearance = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer toastTimer;
    private bool desktopAcrylic;
    internal ClipboardPageMode Mode { get; private set; } = ClipboardPageMode.Main;
    internal bool OwnedPickerOpen { get; private set; }
    internal Func<ClipEntry, Task>? PasteRequested;
    internal Action? Dismiss;
    internal event Action? SettingsRequested, OpenMainRequested;
    public ClipboardPage()
    {
        InitializeComponent();
        History.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(HistoryWheelChanged), true);
        // ListViewItem marks Enter handled, so a plain KeyDown handler on the list never sees it.
        foreach (var list in new ListViewBase[] { Library, History }) list.AddHandler(KeyDownEvent, new KeyEventHandler(HistoryKeyDown), true);
        Library.SizeChanged += (_, _) => FitLibrary();
        Library.Loaded += (_, _) => FitLibrary();
        Loaded += (_, _) => appearance.ColorValuesChanged += AppearanceChanged;
        Unloaded += (_, _) => appearance.ColorValuesChanged -= AppearanceChanged;
        foreach (var (keys, label) in new[] { (new[] { "Enter" }, "복사"), (new[] { "Delete" }, "삭제"), (new[] { "Ctrl", "F" }, "검색") })
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) KeyHints.Children.Add(new TextBlock { Text = "+", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Resource("TextFillColorTertiaryBrush") });
                KeyHints.Children.Add(new Border { Style = (Style)Application.Current.Resources["Keycap"], Margin = new Thickness(i == 0 ? 8 : 0, 0, 0, 0),
                    Child = new TextBlock { Text = keys[i], FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center } });
            }
            KeyHints.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Resource("TextFillColorSecondaryBrush") });
        }
        toastTimer = DispatcherQueue.CreateTimer();
        toastTimer.Interval = TimeSpan.FromSeconds(4);
        toastTimer.Tick += (_, _) => HideToast();
        UpdateModeVisuals();
    }
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    private void AppearanceChanged(Windows.UI.ViewManagement.UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (desktopAcrylic) UpdatePanelTint(); Refresh(); });
    // Quick panel (A안): one header row of search, collections and tools above a strip
    // of 236 px cards. 12 px panel padding; the strip runs into the right edge.
    internal void UsePanelMode()
    {
        Mode = ClipboardPageMode.Panel;
        MainHeader.Visibility = Toolbar.Visibility = TabsRow.Visibility = AnswerRow.Visibility = StatusLine.Visibility = Visibility.Collapsed;
        PanelHeader.Visibility = Visibility.Visible;
        TabsRow.Children.Remove(CategoryTabs);
        CategoryTabs.Padding = new Thickness(0);
        CategoryTabs.VerticalAlignment = VerticalAlignment.Center;
        PanelTabsHost.Children.Insert(0, CategoryTabs);
        Library.Visibility = Visibility.Collapsed; History.Visibility = Visibility.Visible;
        Surface.Padding = new Thickness(12, 12, 0, 12);
        UpdateModeVisuals();
    }
    internal void UseDesktopAcrylic()
    {
        // Keep the XAML layer clear so Window.SystemBackdrop renders its material
        // and its automatic transparency-off / high-contrast / battery-saver fallback.
        RequestedTheme = ElementTheme.Default;
        desktopAcrylic = true;
        PanelChrome.CornerRadius = new CornerRadius(8);
        UpdatePanelTint();
    }
    private void UpdatePanelTint() => PanelChrome.Background = new SolidColorBrush(appearance.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent))
    { Opacity = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? 0 : 0.035 };
    private bool AiAvailable => controller?.Store.Settings.AiSearchEnabled == true;
    private ClipboardFilter Scope => new() { CategoryId = selectedCategory is "" or "*" ? null : selectedCategory, PinnedOnly = selectedCategory == "*" };
    private ListViewBase ActiveList => Mode == ClipboardPageMode.Main ? Library : History;
    private TextBox SearchBox => Mode == ClipboardPageMode.Main ? Query : PanelQuery;
    private ClipEntry? Selected => (ActiveList.SelectedItem as ClipboardCard)?.Entry;
    internal void Configure(ClipboardController value)
    {
        controller = value;
        controller.Store.Changed += StoreChanged;
        controller.Status += SetStatus;
        Refresh();
    }
    private void StoreChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (closed || !active || controller is null) return;
        if (searchScope is not null && !searchScope.SetEquals(CurrentScopeIds()))
        {
            CancelSearch(); SetStatus("검색 범위가 바뀌었습니다. 다시 검색해 주세요.");
        }
        if (aiMode && !AiAvailable) SetMode(false);
        Refresh();
    });
    private string[] CurrentScopeIds()
    {
        var scope = Scope;
        return controller!.Store.Entries.Where(x => scope.MatchesMetadata(x) && controller.Store.InCategory(x, scope.CategoryId)).Select(x => x.Id).ToArray();
    }
    private void SetStatus(string message) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closed || !active) return;
        StatusText.Text = message;
        // The quick panel has no status line; messages show briefly as a toast.
        if (Mode == ClipboardPageMode.Panel) ShowToast(message);
    });
    private void ShowToast(string message, string glyph = "")
    {
        toastTimer.Stop();
        if (message.Length == 0) { HideToast(); return; }
        PanelToastText.Text = message; PanelToastGlyph.Glyph = glyph;
        PanelToast.Opacity = 1;
        toastTimer.Start();
    }
    private void HideToast() { toastTimer.Stop(); PanelToast.Opacity = 0; }
    // The panel opens fresh on the newest record with keyboard focus on that card,
    // so arrow keys and Enter work immediately; Ctrl+F moves to search.
    internal void ResetForOpen()
    {
        active = true;
        CancelSearch();
        filters = new(); UpdateFilterIndicator();
        selectedCategory = "";
        if (aiMode) { aiMode = false; UpdateModeVisuals(); }
        SearchBox.Text = "";
        StatusText.Text = ""; HideToast();
        categorySignature = "";
        pendingCardFocus = true;
        Refresh();
    }
    // Back after a failed paste: keep the chosen card focused so Enter retries it.
    internal void Resume() { active = true; restoreCardFocus = true; Refresh(); }
    internal void Suspend()
    {
        active = false; CancelSearch(); filter?.Cancel(); currentDialog?.Hide(); HideToast();
        foreach (var flyout in openFlyouts.ToArray()) flyout.Hide();
    }
    private void CancelSearch()
    {
        ++searchRevision; search?.Cancel(); resultIds = null; resultEvidence = null; searchScope = null;
        if (Answer is null) return;
        Answer.Text = ""; AnswerContainer.Visibility = Visibility.Collapsed;
        PanelAnswerText.Text = ""; PanelAnswerCard.Visibility = Visibility.Collapsed;
        Searching.Visibility = CancelButton.Visibility = Visibility.Collapsed;
        SearchButton.IsEnabled = true;
    }
    // Main mode shows the answer as a banner; the quick panel shows it as the first card.
    private void ShowAnswer(string text, bool searching = false, int evidence = -1)
    {
        if (Mode == ClipboardPageMode.Main)
        {
            Answer.Text = text; AnswerContainer.Visibility = Visibility.Visible;
            return;
        }
        PanelAnswerCard.Visibility = Visibility.Visible;
        PanelAnswerText.Text = text;
        PanelAnswerProgress.IsActive = searching;
        PanelAnswerProgress.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        PanelAnswerSpark.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        PanelAnswerTitle.Text = searching ? "찾는 중" : "AI 답변";
        PanelAnswerMeta.Text = evidence >= 0 ? $"근거 카드 {evidence}개" : "";
        PanelAnswerAction.Text = searching ? "취소" : "일반 검색으로";
    }
    private void UpdateFilterIndicator()
    {
        bool any = filters.Processes.Length + filters.Formats.Length > 0 || filters.From is not null || filters.Through is not null;
        FilterLabel.Text = any ? "필터 적용됨" : "필터";
        PanelFilterDot.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(PanelFilterButton, any ? "필터 적용됨" : "필터");
    }
    private void UpdateSearchHint()
    {
        if (PanelSearchHint is null || PanelQuery is null) return;
        PanelSearchHint.Visibility = PanelQuery.Text.Length == 0 && PanelQuery.FocusState == FocusState.Unfocused && !aiMode ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateModeVisuals()
    {
        NormalPill.Opacity = aiMode ? 0 : 1; AiPill.Opacity = aiMode ? 1 : 0;
        NormalModeButton.Background = aiMode ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : Resource("ControlFillColorDefaultBrush");
        AiModeButton.Background = aiMode ? Resource("ControlFillColorDefaultBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AiModeButton.Opacity = AiAvailable || controller is null ? 1 : 0.55;
        string aiTip = AiAvailable ? "Shift+Enter로도 AI 검색을 시작합니다." : "클립보드 설정에서 AI 검색을 켜면 사용할 수 있습니다.";
        ToolTipService.SetToolTip(AiModeButton, aiTip);
        FilterButton.Visibility = aiMode ? Visibility.Collapsed : Visibility.Visible;
        SearchButton.Visibility = aiMode ? Visibility.Visible : Visibility.Collapsed;
        Query.PlaceholderText = aiMode ? "찾고 싶은 내용을 문장으로 입력하세요" : "기록 검색";
        PanelAiToggle.IsChecked = aiMode;
        PanelAiToggle.Opacity = AiAvailable || controller is null ? 1 : 0.55;
        PanelAiSpark.Fill = Resource(aiMode ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        ToolTipService.SetToolTip(PanelAiToggle, aiTip);
        PanelFilterButton.Visibility = aiMode ? Visibility.Collapsed : Visibility.Visible;
        PanelQuery.PlaceholderText = aiMode ? "찾을 내용을 문장으로 입력" : "검색";
        UpdateSearchHint();
    }
    private void SetMode(bool ai)
    {
        if (ai && !AiAvailable) { SetStatus("클립보드 설정에서 AI 검색을 켜면 사용할 수 있습니다."); ai = false; }
        if (ai == aiMode) { UpdateModeVisuals(); return; }
        aiMode = ai; CancelSearch(); UpdateModeVisuals(); Refresh();
    }
    private void NormalModeClick(object sender, RoutedEventArgs e) => SetMode(false);
    private void AiModeClick(object sender, RoutedEventArgs e) => SetMode(true);
    private void PanelAiClick(object sender, RoutedEventArgs e)
    {
        SetMode(PanelAiToggle.IsChecked == true);
        if (aiMode) PanelQuery.Focus(FocusState.Programmatic);
    }
    private void PanelQueryFocusChanged(object sender, RoutedEventArgs e)
    {
        PanelSearchRing.Opacity = PanelQuery.FocusState == FocusState.Unfocused ? 0 : 1;
        UpdateSearchHint();
    }
    private void PanelAnswerActionClick(object sender, RoutedEventArgs e)
    {
        if (search is not null) CancelClick(sender, e);
        else NormalModeClick(sender, e);
    }
    private void PanelMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var settings = new MenuFlyoutItem { Text = "클립보드 설정", Icon = new FontIcon { Glyph = "" } }; settings.Click += SettingsClick;
        var main = new MenuFlyoutItem { Text = "메인 창 열기", Icon = new FontIcon { Glyph = "" } }; main.Click += OpenMainClick;
        menu.Items.Add(settings); menu.Items.Add(main);
        ShowFlyout(menu, (FrameworkElement)sender);
    }
    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var card in e.RemovedItems.OfType<ClipboardCard>()) card.IsCurrent = false;
        foreach (var card in e.AddedItems.OfType<ClipboardCard>()) card.IsCurrent = true;
    }
    private void CardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipboardCard card) card.IsHovered = true;
    }
    private void CardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipboardCard card) card.IsHovered = false;
    }
    private async void CardPinClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipboardCard card)
            await RunAction(card.Entry, x => controller!.Store.SetPinnedAsync(x, !x.Pinned));
    }
    private void UpdateTabs()
    {
        if (controller is null) return;
        string signature = string.Join("|", controller.Store.Categories.Select(x => x.Id + ":" + x.Name));
        if (signature == categorySignature && CategoryTabs.Items.Count > 0) return;
        categorySignature = signature;
        if (selectedCategory is not ("" or "*") && !controller.Store.Categories.Any(x => x.Id == selectedCategory)) { selectedCategory = ""; CancelSearch(); }
        updatingTabs = true;
        CategoryTabs.Items.Clear();
        foreach (var category in new[] { new ClipCategory("", "전체"), new ClipCategory("*", "고정") }.Concat(controller.Store.Categories))
        {
            var item = new SelectorBarItem { Text = category.Name, Tag = category };
            if (Mode == ClipboardPageMode.Panel) item.Style = (Style)Resources["PanelTabItem"];
            if (category.Id is not ("" or "*")) item.ContextRequested += CategoryContextRequested;
            CategoryTabs.Items.Add(item);
            if (category.Id == selectedCategory) CategoryTabs.SelectedItem = item;
        }
        updatingTabs = false;
    }
    private (string Label, string Background, string Foreground)? EvidenceBadge(string id)
    {
        if (resultEvidence is null || !resultEvidence.TryGetValue(id, out var kind)) return null;
        return kind switch
        {
            "content" => ("내용 확인", "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            "filename" => ("파일 이름 일치", "SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
            "image_tag" => ("이미지 태그 일치", "SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
            _ => ("기록 정보 일치", "SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush")
        };
    }
    private Brush TypeBrush(string format)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        if (format == "Url") return Resource("AccentTextFillColorPrimaryBrush");
        var (r, g, b) = (format, dark) switch
        {
            ("Image", true) => (79, 209, 217), ("Image", false) => (3, 131, 135),
            ("Files", true) => (242, 192, 76), ("Files", false) => (168, 107, 0),
            (_, true) => (200, 200, 200), (_, false) => (97, 97, 97)
        };
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)r, (byte)g, (byte)b));
    }
    private string DayKey(ClipEntry entry)
    {
        var day = entry.CreatedAt.ToLocalTime().Date;
        return day == DateTime.Today ? "오늘" : day == DateTime.Today.AddDays(-1) ? "어제" : day.ToString(day.Year == DateTime.Today.Year ? "M월 d일 (ddd)" : "yyyy년 M월 d일 (ddd)", Korean);
    }
    private async void Refresh(bool debounce = false)
    {
        if (controller is null || closed || !active) return;
        filter?.Cancel();
        using var filtering = new CancellationTokenSource(); filter = filtering;
        try
        {
            UpdateTabs();
            UpdateModeVisuals();
            string? selected = Selected?.Id;
            var options = aiMode ? Scope : filters with { CategoryId = Scope.CategoryId, PinnedOnly = Scope.PinnedOnly };
            string query = aiMode ? "" : SearchBox.Text;
            var ids = resultIds;
            if (debounce) await Task.Delay(200, filtering.Token);
            var found = await Task.Run(() => controller.Store.FindAsync(query, options, filtering.Token), filtering.Token);
            IEnumerable<ClipEntry> entries = ids is null ? found.OrderByDescending(x => Mode == ClipboardPageMode.Panel && x.Pinned).ThenByDescending(x => x.CreatedAt)
                : found.Where(x => ids.Contains(x.Id)).OrderBy(x => Array.IndexOf(ids, x.Id));
            var formats = await Task.Run(async () =>
            {
                var rows = new List<(ClipEntry, string)>();
                foreach (var entry in entries)
                {
                    filtering.Token.ThrowIfCancellationRequested();
                    rows.Add((entry, await controller.Store.GetFormatAsync(entry, filtering.Token)));
                }
                return rows;
            }, filtering.Token);
            filtering.Token.ThrowIfCancellationRequested();
            if (!active || closed) return;
            var cards = formats.Select(x =>
            {
                var badge = EvidenceBadge(x.Item1.Id);
                return new ClipboardCard(x.Item1, x.Item2)
                {
                    TypeBrush = TypeBrush(x.Item2),
                    Badge = badge?.Label ?? "", BadgeBackground = badge is { } b1 ? Resource(b1.Background) : null, BadgeForeground = badge is { } b2 ? Resource(b2.Foreground) : null
                };
            }).ToList();
            if (Mode == ClipboardPageMode.Main)
            {
                List<ClipGroup> groups = ids is not null ? [new ClipGroup("AI 검색 결과", cards)] : cards.GroupBy(x => DayKey(x.Entry)).Select(g => new ClipGroup(g.Key, g)).ToList();
                Library.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
            }
            else History.ItemsSource = cards;
            var target = cards.FirstOrDefault(x => x.Entry.Id == selected && !pendingCardFocus) ?? cards.FirstOrDefault();
            ActiveList.SelectedItem = target;
            bool any = controller.Store.Entries.Count > 0;
            EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyLabel.Text = any ? "조건에 맞는 기록이 없습니다." : "아직 복사한 기록이 없습니다.";
            EmptyHint.Text = any ? "검색어나 필터, 탭을 바꿔 보세요."
                : $"텍스트, 이미지, 파일을 복사하면 여기에 쌓입니다. 어느 앱에서든 {ClipboardSettingsPage.FormatHotkey(controller.Store.Settings)}로 빠른 패널을 열 수 있습니다.";
            var settings = controller.Store.Settings;
            PauseLabel.Text = settings.Paused ? "기록 재개" : "기록 일시정지";
            PauseGlyph.Glyph = PanelPauseGlyph.Glyph = settings.Paused ? "" : "";
            ToolTipService.SetToolTip(PanelPauseButton, PauseLabel.Text);
            AutomationProperties.SetName(PanelPauseButton, PauseLabel.Text);
            PanelPausedChip.Visibility = settings.Paused ? Visibility.Visible : Visibility.Collapsed;
            RecordDot.Fill = Resource(settings.Paused ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
            if (Mode == ClipboardPageMode.Main)
            {
                bool narrowed = query.Length > 0 || ids is not null || options.Processes.Length + options.Formats.Length > 0 || options.From is not null || options.Through is not null || selectedCategory.Length > 0;
                StatusText.Text = (settings.Paused ? "기록 일시정지" : "기록 중") + $" · 전체 {controller.Store.Entries.Count:N0}개" + (narrowed ? $" · {cards.Count:N0}개 표시" : "");
            }
            // Focus moves only once the new cards exist; focusing before the list is
            // replaced would land on a container that is about to be discarded.
            if (pendingCardFocus || restoreCardFocus)
            {
                pendingCardFocus = restoreCardFocus = false;
                if (target is not null) FocusCard(target);
                else SearchBox.Focus(FocusState.Programmatic);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetStatus(error.Message); }
        finally { if (filter == filtering) filter = null; }
    }
    private void FitLibrary()
    {
        if (Library.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        double width = Library.ActualWidth - Library.Padding.Left - Library.Padding.Right - 2;
        if (width <= 0) return;
        int columns = width >= 1100 ? 4 : width >= 720 ? 3 : width >= 460 ? 2 : 1;
        panel.ItemWidth = Math.Floor(width / columns);
    }
    private void QueryChanged(object sender, TextChangedEventArgs e) { CancelSearch(); UpdateSearchHint(); if (!aiMode) Refresh(true); }
    private void CategoryChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (updatingTabs || sender.SelectedItem?.Tag is not ClipCategory category) return;
        selectedCategory = category.Id; CancelSearch(); Refresh();
    }
    private async Task RunAction(ClipEntry? entry, Func<ClipEntry, Task> action)
    {
        if (entry is null) return;
        try { await action(entry); } catch (Exception error) { SetStatus(error.Message); }
    }
    private Task PasteAsync(ClipEntry entry) => RunAction(entry, x => PasteRequested?.Invoke(x) ?? controller!.CopyAsync(x));
    private async void CardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipboardCard card) { e.Handled = true; await PasteAsync(card.Entry); }
    }
    private async void HistoryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Keys on a card's own buttons (the ⋯ menu) keep their normal meaning.
        if (e.OriginalSource is not SelectorItem item || ((ListViewBase)sender).ItemFromContainer(item) is not ClipboardCard card) return;
        if (e.Key == VirtualKey.Enter) { e.Handled = true; await PasteAsync(card.Entry); }
        if (e.Key == VirtualKey.Delete) { e.Handled = true; await RunAction(card.Entry, x => controller!.Store.DeleteAsync(x)); }
    }
    private void FocusSearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!active || closed || currentDialog is not null || OwnedPickerOpen) return;
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }
    private void PageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { e.Handled = true; CancelSearch(); Dismiss?.Invoke(); }
        if (e.Key == VirtualKey.Down && SearchBox.FocusState != FocusState.Unfocused && ActiveList.SelectedItem is ClipboardCard card) { FocusCard(card); e.Handled = true; }
    }
    // Keyboard focus on the card itself, so arrow keys move between cards and Enter pastes.
    private void FocusCard(ClipboardCard card)
    {
        ActiveList.ScrollIntoView(card);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ActiveList.ContainerFromItem(card) is SelectorItem item) item.Focus(FocusState.Keyboard);
            else ActiveList.Focus(FocusState.Keyboard);
        });
    }
    private async void QueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        // Shift+Enter always asks AI; plain Enter keeps whichever mode the AI toggle shows.
        bool ai = aiMode || (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (ai && !AiAvailable) { SetStatus("클립보드 설정에서 AI 검색을 켜면 사용할 수 있습니다. 일반 검색 결과를 보여 줍니다."); ai = false; }
        if (ai != aiMode) { aiMode = ai; UpdateModeVisuals(); }
        if (ai) await BeginSearchAsync();
        else { CancelSearch(); pendingCardFocus = true; Refresh(); }
    }
    private void HistoryWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(History).Properties;
        var scroller = FindScrollViewer(History);
        if (scroller is null || point.MouseWheelDelta == 0) return;
        double delta = point.IsHorizontalMouseWheel ? -point.MouseWheelDelta : point.MouseWheelDelta;
        scroller.ChangeView(Math.Clamp(scroller.HorizontalOffset + delta, 0, scroller.ScrollableWidth), null, null, true);
        e.Handled = true;
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private async void PauseClick(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        try
        {
            bool paused = !controller.Store.Settings.Paused;
            await controller.UpdateSettingsAsync(controller.Store.Settings with { Paused = paused });
            if (Mode == ClipboardPageMode.Panel && active)
                ShowToast(paused ? "기록을 일시정지했습니다. 새로 복사한 내용은 저장하지 않습니다." : "기록을 다시 시작했습니다.", paused ? "" : "");
        }
        catch (Exception error) { SetStatus(error.Message); }
    }
    private async void SearchClick(object sender, RoutedEventArgs e) => await BeginSearchAsync();
    private async Task BeginSearchAsync()
    {
        if (controller is null || !aiMode || !active) return;
        CancelSearch(); int revision = searchRevision;
        if (searchTask is not null) await searchTask;
        if (revision != searchRevision || !active) return;
        searchTask = SearchAsync(revision); await searchTask;
    }
    private async Task SearchAsync(int revision)
    {
        using var cancellation = new CancellationTokenSource(); search = cancellation;
        SearchButton.IsEnabled = false; CancelButton.Visibility = Searching.Visibility = Visibility.Visible;
        StatusText.Text = "선택한 범위에서 AI로 찾는 중…";
        if (Mode == ClipboardPageMode.Panel) ShowAnswer("선택한 범위의 기록과 파일 내용을 읽고 있습니다.", searching: true);
        try
        {
            var ids = selectedCategory.Length == 0 ? null : CurrentScopeIds();
            searchScope = ids?.ToHashSet(StringComparer.Ordinal);
            var result = await controller!.Search.SearchClipboardAsync(SearchBox.Text, ids, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (revision != searchRevision || !active || !aiMode) return;
            resultIds = result.ItemIds; resultEvidence = result.Evidence;
            ShowAnswer(result.Answer + (result.Limitations.Length == 0 ? "" : "\n" + string.Join("\n", result.Limitations)), evidence: result.ItemIds.Length);
            pendingCardFocus = true; Refresh();
            SetStatus(result.ItemIds.Length == 0 ? "AI 검색 결과가 없습니다." : "AI 검색 완료 · 카드를 선택하고 Enter를 누르세요.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (revision == searchRevision && active) ShowAnswer(error.Message + " 다시 검색하거나 일반 검색을 사용해 주세요.");
        }
        finally
        {
            if (search == cancellation) search = null;
            if (revision == searchRevision) { SearchButton.IsEnabled = true; CancelButton.Visibility = Searching.Visibility = Visibility.Collapsed; }
        }
    }
    private void CancelClick(object sender, RoutedEventArgs e) { CancelSearch(); SetStatus("검색을 취소했습니다."); Refresh(); }
    private void ShowFlyout(FlyoutBase flyout, FrameworkElement anchor)
    {
        openFlyouts.Add(flyout); flyout.Closed += (_, _) => openFlyouts.Remove(flyout);
        flyout.ShowAt(anchor);
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (currentDialog is not null) return ContentDialogResult.None;
        currentDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { currentDialog = null; }
    }
    private void CardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement element && element.DataContext is ClipboardCard card) { args.Handled = true; CardMenu(element, card.Entry); }
    }
    private void CardMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ClipboardCard card) CardMenu(element, card.Entry);
    }
    private void CardMenu(FrameworkElement anchor, ClipEntry entry)
    {
        ActiveList.SelectedItem = Mode == ClipboardPageMode.Main
            ? Library.Items.OfType<ClipboardCard>().FirstOrDefault(x => x.Entry.Id == entry.Id)
            : History.Items.OfType<ClipboardCard>().FirstOrDefault(x => x.Entry.Id == entry.Id);
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Func<ClipEntry, Task> action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += async (_, _) => await RunAction(entry, action);
            menu.Items.Add(item);
        }
        if (Mode == ClipboardPageMode.Panel) Add("붙여넣기", "", PasteAsync);
        Add("복사", "", x => controller!.CopyAsync(x));
        Add("미리보기", "", PreviewAsync);
        Add("모음 지정", "", AssignCategoriesAsync);
        Add(entry.Pinned ? "고정 해제" : "고정", entry.Pinned ? "" : "", x => controller!.Store.SetPinnedAsync(x, !x.Pinned));
        if (entry.Kind == "Image" && controller!.Store.Settings.AutoTagImages)
            Add(entry.ImageTags is null ? "이미지 태그 만들기" : "이미지 태그 다시 만들기", "", x => { controller.RetagImage(x); return Task.CompletedTask; });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("삭제", "", x => controller!.Store.DeleteAsync(x));
        ShowFlyout(menu, anchor);
    }
    private async Task PreviewAsync(ClipEntry entry)
    {
        if (controller is null) return;
        var panel = new StackPanel { Spacing = 14, Width = 552 };
        if (entry.Kind == "Image")
        {
            panel.Children.Add(new Border { CornerRadius = new CornerRadius(4), BorderBrush = Resource("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), MaxHeight = 280,
                Child = new Image { Source = new BitmapImage(new Uri(controller.Store.PayloadPath(entry, entry.Attachments[0]))), Stretch = Stretch.Uniform } });
        }
        else
        {
            string text;
            if (entry.Kind == "Text")
            {
                using var reader = File.OpenText(controller.Store.PayloadPath(entry, entry.Attachments[0]));
                var buffer = new char[16000]; int count = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                text = new string(buffer, 0, count) + (reader.EndOfStream ? "" : "\n… 미리보기 생략 (복사하면 전체 내용을 씁니다)");
            }
            else text = string.Join("\n", entry.Attachments.Select(x => $"{x.OriginalName} · {x.Length:N0} bytes"));
            panel.Children.Add(new ScrollViewer { MaxHeight = 300, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } });
        }
        var card = new ClipboardCard(entry, await controller.Store.GetFormatAsync(entry));
        panel.Children.Add(new TextBlock { Text = $"{card.AppLabel} · {entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {card.FormatLabel} · {entry.Attachments.Sum(x => x.Length):N0} bytes",
            FontSize = 12, Foreground = Resource("TextFillColorSecondaryBrush") });
        if (entry.Kind == "Image") panel.Children.Add(TagBox(entry));
        if (!active) return;
        var dialog = new ContentDialog
        {
            Title = "미리보기", Content = panel, XamlRoot = XamlRoot,
            PrimaryButtonText = Mode == ClipboardPageMode.Panel ? "붙여넣기" : "복사",
            SecondaryButtonText = entry.Pinned ? "고정 해제" : "고정", CloseButtonText = "닫기", DefaultButton = ContentDialogButton.Primary
        };
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary) await PasteAsync(entry);
        else if (result == ContentDialogResult.Secondary) await RunAction(entry, x => controller.Store.SetPinnedAsync(x, !x.Pinned));
    }
    private Border TagBox(ClipEntry entry)
    {
        var box = new StackPanel { Spacing = 10 };
        var header = new Grid();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new Viewbox { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Child = new Microsoft.UI.Xaml.Shapes.Path
        {
            Fill = Resource("AccentTextFillColorPrimaryBrush"),
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), "M7,1.5 L8.4,5.6 12.5,7 8.4,8.4 7,12.5 5.6,8.4 1.5,7 5.6,5.6 Z M12.6,10.4 L13.2,12 14.8,12.6 13.2,13.2 12.6,14.8 12,13.2 10.4,12.6 12,12 Z")
        } });
        title.Children.Add(new TextBlock { Text = "이미지 태그", FontWeight = FontWeights.SemiBold });
        header.Children.Add(title);
        if (controller!.Store.Settings.AutoTagImages)
        {
            var retry = new Button { Style = (Style)Application.Current.Resources["SubtleTextButton"], HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8, 2, 8, 2),
                Content = new TextBlock { Text = entry.ImageTags is null ? "태그 만들기" : "다시 만들기", FontSize = 13 } };
            retry.Click += (_, _) => { try { controller.RetagImage(entry); currentDialog?.Hide(); } catch (Exception error) { SetStatus(error.Message); } };
            header.Children.Add(retry);
        }
        box.Children.Add(header);
        if (entry.ImageTags is { } tags)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var tag in tags.Tags.Take(12))
                line.Children.Add(new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 3, 9, 4), Background = Resource("SubtleFillColorSecondaryBrush"),
                    Child = new TextBlock { Text = tag, FontSize = 12 } });
            box.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, Content = line });
            var grid = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            int row = 0;
            void Pair(string name, string value, bool mono = false)
            {
                if (value.Length == 0) return;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var key = new TextBlock { Text = name, FontSize = 13, Foreground = Resource("TextFillColorSecondaryBrush") }; Grid.SetRow(key, row); grid.Children.Add(key);
                var text = new TextBlock { Text = value, FontSize = mono ? 12 : 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis };
                if (mono) text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                Grid.SetRow(text, row); Grid.SetColumn(text, 1); grid.Children.Add(text); row++;
            }
            Pair("설명", tags.Description); Pair("인식한 글자", tags.Text, true);
            box.Children.Add(grid);
        }
        else box.Children.Add(new TextBlock
        {
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Resource("TextFillColorSecondaryBrush"),
            Text = entry.ImageTagState switch
            {
                "pending" or "processing" => "태그를 만드는 중입니다.",
                "failed" => "태그를 만들지 못했습니다. Antigravity 연결을 확인한 뒤 다시 만들어 주세요.",
                _ => controller.Store.Settings.AutoTagImages ? "아직 태그가 없습니다." : "클립보드 설정에서 이미지 자동 태깅을 켜면 태그가 만들어집니다."
            }
        });
        return new Border { Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(4), Background = Resource("CardBackgroundFillColorSecondaryBrush"),
            BorderBrush = Resource("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), Child = box };
    }
    private async Task AssignCategoriesAsync(ClipEntry entry)
    {
        if (controller is null) return;
        var panel = new StackPanel { Spacing = 8 };
        var selected = controller.Store.CategoryIds(entry.Id);
        var checks = controller.Store.Categories.Select(x => new CheckBox { Content = x.Name, Tag = x.Id, IsChecked = selected.Contains(x.Id) }).ToArray();
        foreach (var check in checks) panel.Children.Add(check);
        if (checks.Length == 0) panel.Children.Add(new TextBlock { Text = "탭 옆의 + 버튼으로 모음을 먼저 만들어 주세요.", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = "모음 지정", Content = new ScrollViewer { Content = panel, MaxHeight = 300 }, PrimaryButtonText = "저장", CloseButtonText = "취소", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await controller.Store.SetCategoriesAsync(entry.Id, checks.Where(x => x.IsChecked == true).Select(x => (string)x.Tag));
    }
    private async void AddCategoryClick(object sender, RoutedEventArgs e) => await EditCategoryAsync(null);
    private void CategoryContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not SelectorBarItem { Tag: ClipCategory category } element || category.Id is "" or "*") return;
        args.Handled = true;
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "이름 바꾸기", Icon = new FontIcon { Glyph = "" } }; rename.Click += async (_, _) => await EditCategoryAsync(category);
        var delete = new MenuFlyoutItem { Text = "모음 삭제 (기록은 유지)", Icon = new FontIcon { Glyph = "" } };
        delete.Click += async (_, _) => { try { await controller!.Store.DeleteCategoryAsync(category.Id); } catch (Exception error) { SetStatus(error.Message); } };
        menu.Items.Add(rename); menu.Items.Add(delete); ShowFlyout(menu, element);
    }
    private async Task EditCategoryAsync(ClipCategory? category)
    {
        var name = new TextBox { Text = category?.Name ?? "", PlaceholderText = "모음 이름", MaxLength = 60, Width = 360 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Resource("SystemFillColorCriticalBrush") };
        var content = new StackPanel { Spacing = 8 }; content.Children.Add(name); content.Children.Add(error);
        var dialog = new ContentDialog { Title = category is null ? "모음 추가" : "모음 이름 바꾸기", Content = content, PrimaryButtonText = "저장", CloseButtonText = "취소", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try { await controller!.Store.SaveCategoryAsync(name.Text, category?.Id); }
            catch (Exception exception) { args.Cancel = true; error.Text = exception.Message.Replace("카테고리", "모음"); }
            finally { deferral.Complete(); }
        };
        try { await ShowDialogAsync(dialog); } catch (Exception exception) { SetStatus(exception.Message); }
    }
    private void FiltersClick(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        var panel = new StackPanel { Spacing = 10, Width = 290 };
        panel.Children.Add(new TextBlock { Text = "복사한 앱", FontWeight = FontWeights.SemiBold });
        var processes = controller.Store.Entries.Select(x => x.SourceApp).Concat(filters.Processes).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Select(x => new CheckBox { Content = x, IsChecked = filters.Processes.Contains(x, StringComparer.OrdinalIgnoreCase) }).ToArray();
        var apps = new StackPanel(); foreach (var check in processes) apps.Children.Add(check);
        panel.Children.Add(new ScrollViewer { Content = apps, MaxHeight = 120 });
        panel.Children.Add(new TextBlock { Text = "형식 · 선택하지 않으면 전체", FontWeight = FontWeights.SemiBold });
        var kinds = new[] { ("Text", "텍스트"), ("Image", "이미지"), ("Url", "URL"), ("Files", "파일") }
            .Select(x => new CheckBox { Content = x.Item2, Tag = x.Item1, IsChecked = filters.Formats.Contains(x.Item1) }).ToArray();
        foreach (var check in kinds) panel.Children.Add(check);
        var preset = new ComboBox { Header = "복사한 날짜", ItemsSource = new[] { "전체", "오늘", "최근 7일", "최근 30일", "직접 지정" }, SelectedIndex = filters.From is null && filters.Through is null ? 0 : 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        var from = new CalendarDatePicker { PlaceholderText = "시작일", Date = filters.From is { } start ? new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue)) : null };
        var through = new CalendarDatePicker { PlaceholderText = "종료일", Date = filters.Through is { } end ? new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue)) : null };
        from.Visibility = through.Visibility = preset.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        preset.SelectionChanged += (_, _) => from.Visibility = through.Visibility = preset.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(preset); panel.Children.Add(from); panel.Children.Add(through);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "적용", Style = (Style)Application.Current.Resources["AccentButtonStyle"] }; var reset = new Button { Content = "초기화" };
        buttons.Children.Add(apply); buttons.Children.Add(reset); panel.Children.Add(buttons);
        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(160, Math.Min(500, XamlRoot.Size.Height - 80)) } };
        apply.Click += (_, _) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            DateOnly? first = preset.SelectedIndex switch { 1 => today, 2 => today.AddDays(-6), 3 => today.AddDays(-29), 4 => from.Date is { } d ? DateOnly.FromDateTime(d.DateTime) : null, _ => null };
            DateOnly? last = preset.SelectedIndex is 1 or 2 or 3 ? today : preset.SelectedIndex == 4 && through.Date is { } endDate ? DateOnly.FromDateTime(endDate.DateTime) : null;
            if (first > last) { error.Text = "시작일은 종료일보다 늦을 수 없습니다."; return; }
            filters = new() { Processes = processes.Where(x => x.IsChecked == true).Select(x => (string)x.Content).ToArray(), Formats = kinds.Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToArray(), From = first, Through = last };
            UpdateFilterIndicator();
            flyout.Hide(); Refresh();
        };
        reset.Click += (_, _) => { filters = new(); UpdateFilterIndicator(); flyout.Hide(); Refresh(); };
        ShowFlyout(flyout, Mode == ClipboardPageMode.Main ? FilterButton : PanelFilterButton);
    }
    private void SurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SearchColumn is null) return;
        SearchColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width - (Mode == ClipboardPageMode.Panel ? 560 : 420), 180, 360));
    }
    private async void AppIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not ClipboardCard card) return;
        image.Source = null;
        var fallback = (image.Parent as Grid)?.Children.OfType<FontIcon>().FirstOrDefault();
        if (fallback is not null) fallback.Visibility = Visibility.Visible;
        var icon = await ClipboardAppIcons.GetAsync(card.Entry);
        if (!image.IsLoaded || !ReferenceEquals(image.DataContext, card)) return;
        image.Source = icon;
        if (fallback is not null) fallback.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AppIconDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => AppIconLoaded(sender, new RoutedEventArgs());
    private void ThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not ClipboardCard card || card.Entry.Kind != "Image" || controller is null) return;
        try { image.Source = new BitmapImage { DecodePixelWidth = 480, UriSource = new Uri(controller.Store.PayloadPath(card.Entry, card.Entry.Attachments[0])) }; }
        catch (Exception error) { SetStatus(error.Message); }
    }
    private void ThumbnailUnloaded(object sender, RoutedEventArgs e) { if (sender is Image image) image.Source = null; }
    private void ThumbnailDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Image image) { image.Source = null; ThumbnailLoaded(image, new RoutedEventArgs()); }
    }
    private void SettingsClick(object sender, RoutedEventArgs e) { Dismiss?.Invoke(); SettingsRequested?.Invoke(); }
    private void OpenMainClick(object sender, RoutedEventArgs e) { Dismiss?.Invoke(); OpenMainRequested?.Invoke(); }
    internal async Task ShutdownAsync()
    {
        closed = true; Suspend();
        if (searchTask is not null) await searchTask;
        if (controller is not null) { controller.Store.Changed -= StoreChanged; controller.Status -= SetStatus; }
    }
}
