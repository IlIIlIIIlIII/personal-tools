using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace PersonalTools;

// Settings apply as soon as they change, like the Windows Settings app.
public sealed partial class ClipboardSettingsPage : UserControl
{
    private static readonly long[] TotalChoices = [1L << 30, 2L << 30, 5L << 30, 10L << 30, 20L << 30, 50L << 30, 100L << 30];
    private static readonly long[] ItemChoices = [10L << 20, 25L << 20, 50L << 20, 100L << 20, 250L << 20, 500L << 20, 1L << 30];
    private ClipboardController? controller;
    private Window? owner;
    private bool updating, saving;
    internal event Action? BackRequested, BridgeRequested;
    public ClipboardSettingsPage() { InitializeComponent(); }
    internal void Configure(ClipboardController value, Window window)
    {
        controller = value; owner = window;
        controller.Store.Changed += () => DispatcherQueue.TryEnqueue(() => { if (!saving) Load(); });
        Load();
    }
    internal static string FormatHotkey(ClipboardSettings s) =>
        ((s.HotkeyModifiers & 2) != 0 ? "Ctrl+" : "") + ((s.HotkeyModifiers & 4) != 0 ? "Shift+" : "") + ((s.HotkeyModifiers & 1) != 0 ? "Alt+" : "") + (char)s.HotkeyKey;
    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0} MB";
    private static Border Keycap(string text) => new()
    {
        Style = (Style)Application.Current.Resources["Keycap"],
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }
    };
    private static void ShowKeys(Panel panel, string shortcut)
    {
        panel.Children.Clear();
        var parts = shortcut.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) panel.Children.Add(new TextBlock { Text = "+", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
            panel.Children.Add(Keycap(parts[i]));
        }
    }
    private static void Choices(ComboBox box, long[] values, long current)
    {
        var list = values.Contains(current) ? values : values.Append(current).Order().ToArray();
        box.ItemsSource = list.Select(Size).ToArray();
        box.Tag = list;
        box.SelectedIndex = Array.IndexOf(list, current);
    }
    internal void Load()
    {
        if (controller is null) return;
        var s = controller.Store.Settings;
        updating = true;
        RecordSwitch.IsOn = !s.Paused;
        ShowKeys(HotkeyKeys, FormatHotkey(s));
        ExcludedSummary.Text = s.ExcludedApps.Length == 0 ? "없음" : s.ExcludedApps.Length == 1 ? s.ExcludedApps[0] : $"{s.ExcludedApps[0]} 외 {s.ExcludedApps.Length - 1}개";
        ArchivePathText.Text = controller.Store.Root;
        ToolTipService.SetToolTip(ArchivePathText, controller.Store.Root);
        Choices(TotalBox, TotalChoices, s.TotalLimitBytes);
        Choices(ItemBox, ItemChoices, s.ItemLimitBytes);
        AiSwitch.IsOn = s.AiSearchEnabled;
        TagSwitch.IsOn = s.AutoTagImages;
        updating = false;
        _ = MeasureAsync();
    }
    private async Task MeasureAsync()
    {
        if (controller is null) return;
        string root = controller.Store.Root; long limit = controller.Store.Settings.TotalLimitBytes;
        long used = await Task.Run(() =>
        {
            try { return Directory.Exists(root) ? new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(x => x.Length) : 0; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return -1; }
        });
        if (used < 0) { UsageText.Text = "사용량을 확인하지 못했습니다"; return; }
        UsageText.Text = $"{Size(Math.Max(used, 1L << 20))} 사용 중 · 가득 차면 새 기록을 저장하지 않습니다.";
        UsageBar.Value = Math.Clamp(used / (double)limit, 0, 1);
    }
    private async Task SaveAsync(Func<ClipboardSettings, ClipboardSettings> change, string? done = null)
    {
        if (controller is null || updating) return;
        saving = true;
        try
        {
            await controller.UpdateSettingsAsync(change(controller.Store.Settings));
            if (done is not null) Show(done, "", InfoBarSeverity.Success);
        }
        catch (Exception error) { Show("설정을 저장하지 못했습니다", error.Message, InfoBarSeverity.Error); }
        finally { saving = false; Load(); }
    }
    private void Show(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Title = title; MessageBar.Message = message; MessageBar.Severity = severity; MessageBar.IsOpen = true;
    }
    private async void RecordToggled(object sender, RoutedEventArgs e) => await SaveAsync(s => s with { Paused = !RecordSwitch.IsOn });
    private async void AiToggled(object sender, RoutedEventArgs e) => await SaveAsync(s => s with { AiSearchEnabled = AiSwitch.IsOn });
    private async void TagToggled(object sender, RoutedEventArgs e) => await SaveAsync(s => s with { AutoTagImages = TagSwitch.IsOn });
    private async void LimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || sender is not ComboBox { Tag: long[] values, SelectedIndex: >= 0 } box) return;
        long value = values[box.SelectedIndex];
        await SaveAsync(s => box == TotalBox ? s with { TotalLimitBytes = value } : s with { ItemLimitBytes = value });
    }
    private async void ChangeHotkey(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        uint modifiers = 0, key = 0;
        var preview = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 28 };
        var hint = new TextBlock { Text = "Ctrl, Shift, Alt 중 하나 이상과 영문자·숫자를 함께 누르세요.", Style = (Style)Application.Current.Resources["CardDescription"], HorizontalAlignment = HorizontalAlignment.Center };
        var capture = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(8), IsTabStop = true, TabFocusNavigation = KeyboardNavigationMode.Cycle,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], BorderThickness = new Thickness(1) };
        var content = new StackPanel { Spacing = 12, Width = 360 };
        content.Children.Add(preview); content.Children.Add(hint);
        capture.Child = content;
        ShowKeys(preview, FormatHotkey(controller.Store.Settings));
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "빠른 패널 단축키", Content = capture, PrimaryButtonText = "저장", CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false };
        bool Down(VirtualKey k) => (InputKeyboardSource.GetKeyStateForCurrentThread(k) & CoreVirtualKeyStates.Down) != 0;
        dialog.PreviewKeyDown += (_, args) =>
        {
            var k = args.Key;
            if (k is VirtualKey.Escape or VirtualKey.Enter or VirtualKey.Tab) return;
            args.Handled = true;
            bool letter = k is >= VirtualKey.A and <= VirtualKey.Z, digit = k is >= VirtualKey.Number0 and <= VirtualKey.Number9;
            uint mods = (Down(VirtualKey.Menu) ? 1u : 0) | (Down(VirtualKey.Control) ? 2u : 0) | (Down(VirtualKey.Shift) ? 4u : 0);
            if (!(letter || digit)) return;
            if (mods == 0 || Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
            { hint.Text = "Win 키 조합은 창 빠른 전환과 Windows가 사용합니다. Ctrl, Shift, Alt와 함께 눌러 주세요."; return; }
            modifiers = mods; key = (uint)k;
            ShowKeys(preview, FormatHotkey(new ClipboardSettings { HotkeyModifiers = modifiers, HotkeyKey = key }));
            hint.Text = "이 조합으로 저장합니다.";
            dialog.IsPrimaryButtonEnabled = true;
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && key != 0)
            await SaveAsync(s => s with { HotkeyModifiers = modifiers, HotkeyKey = key }, "단축키를 바꿨습니다");
    }
    private async void EditExcluded(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        var box = new TextBox { Header = "실행 파일 이름 (쉼표로 구분)", PlaceholderText = "예: KeePassXC.exe, 1Password.exe", Text = string.Join(", ", controller.Store.Settings.ExcludedApps), Width = 400 };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "기록하지 않을 앱", Content = box, PrimaryButtonText = "저장", CloseButtonText = "취소", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await SaveAsync(s => s with { ExcludedApps = box.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) });
    }
    private async void MoveArchive(object sender, RoutedEventArgs e)
    {
        if (controller is null || owner is null) return;
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            MoveButton.IsEnabled = false;
            Show("보관함을 옮기는 중입니다", "모든 기록을 복사하고 검증한 뒤 새 폴더로 바꿉니다.", InfoBarSeverity.Informational);
            await SaveAsync(s => s with { ArchivePath = folder.Path }, "보관함을 옮겼습니다. 이전 폴더의 사본은 그대로 남아 있습니다.");
        }
        catch (Exception error) { Show("폴더를 바꾸지 못했습니다", error.Message, InfoBarSeverity.Error); }
        finally { MoveButton.IsEnabled = true; }
    }
    private void BackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void OpenBridge(object sender, RoutedEventArgs e) => BridgeRequested?.Invoke();
}
