using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PersonalTools;

public sealed partial class DiagnosticsPage : UserControl
{
    private AudioController? audio;
    private ClipboardController? clipboard;
    private WindowSwitchController? windows;
    private AntigravityPage? bridge;
    private string? logSignature;
    public DiagnosticsPage() { InitializeComponent(); }
    internal void Configure(AudioController audio, ClipboardController clipboard, WindowSwitchController windows, AntigravityPage bridge)
    {
        this.audio = audio; this.clipboard = clipboard; this.windows = windows; this.bridge = bridge;
        audio.Updated += Refresh;
        windows.Changed += Refresh;
        bridge.StateChanged += Refresh;
        Refresh();
    }
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    private static string Tone(string tone) => tone switch { "ok" => "SystemFillColorSuccessBrush", "warn" => "SystemFillColorCautionBrush", "bad" => "SystemFillColorCriticalBrush", _ => "TextFillColorTertiaryBrush" };
    private static void Row(Panel panel, string tone, string label, string value)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Resource(Tone(tone)), VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = label, FontSize = 13 }; Grid.SetColumn(name, 1); row.Children.Add(name);
        var text = new TextBlock { Text = value, FontSize = 13, Foreground = Resource("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 2); row.Children.Add(text);
        panel.Children.Add(row);
    }
    private void Refresh()
    {
        if (audio is null || clipboard is null || windows is null || bridge is null || Visibility != Visibility.Visible) return;
        AudioRows.Children.Clear();
        var snapshot = audio.Current;
        bool installed = AudioController.Installed;
        if (snapshot is null)
        {
            Row(AudioRows, "bad", "오디오 서비스", audio.Error ?? "상태를 읽는 중입니다");
            if (audio.BridgeMissing) Row(AudioRows, "bad", "스피커 참조", "수집 안 됨 · 마이크 반향 제거가 동작하지 않습니다");
        }
        else if (!installed) Row(AudioRows, "", "효과", "설치 전 · 사운드에서 설치할 수 있습니다");
        else
        {
            var r = snapshot.Render; var c = snapshot.Capture;
            Row(AudioRows, !snapshot.EqEnabled ? "" : r.Active && r.Enabled && r.Error == 0 ? "ok" : "warn", "스피커 EQ",
                !snapshot.EqEnabled ? "꺼짐" : r.Active ? $"{r.State} · {r.Rate:N0} Hz" : r.State);
            Row(AudioRows, !snapshot.AecEnabled ? "" : c.Active && c.Enabled && c.Error == 0 && snapshot.Reference ? "ok" : "warn", "마이크 반향 제거",
                !snapshot.AecEnabled ? "꺼짐" : c.Active ? $"{c.State} · {c.Rate:N0} Hz" : c.State);
            if (AudioController.NpuInstalled)
                Row(AudioRows, c.NeuralState == 2 ? "ok" : c.NeuralState < 0 ? "warn" : "", "NPU",
                    c.NeuralState == -2 ? "처리 프로그램이 실행 중이 아님" + (audio.NpuHostError is { } hostError ? " · " + hostError : "")
                    : c.NeuralState < 0 ? $"{c.FailureText} · 자동 재시도 {c.NeuralRestarts}회"
                    : c.NeuralState == 2 ? $"{(snapshot.NpuDevice.Length > 0 ? snapshot.NpuDevice : "NPU")} · DTLN-AEC 실행 중 · 자동 복구 {c.NeuralRestarts}회"
                    : c.NeuralState == 1 ? "모델 준비 중" : "마이크 사용 대기");
            Row(AudioRows, snapshot.Reference ? "ok" : "", "스피커 참조", snapshot.Reference ? "신호 확인" : "소리가 재생되면 확인합니다");
            if (snapshot.Clipping) Row(AudioRows, "bad", "마이크 입력", "과부하 · 입력 게인을 낮춰 주세요");
        }
        if (audio.BridgeFallback is { } bridgePath) Row(AudioRows, "", "UI 브리지", $"앱 폴더에 없어 설치본 사용 · {bridgePath}");
        ClipboardRows.Children.Clear();
        var store = clipboard.Store; var settings = store.Settings;
        Row(ClipboardRows, settings.Paused ? "warn" : "ok", "기록", settings.Paused ? "일시정지" : $"기록 중 · {store.Entries.Count:N0}개");
        int waiting = store.Entries.Count(x => x.Kind == "Image" && x.ImageTagState is "pending" or "processing");
        int failed = store.Entries.Count(x => x.Kind == "Image" && x.ImageTagState == "failed");
        Row(ClipboardRows, !settings.AutoTagImages ? "" : failed > 0 ? "warn" : "ok", "이미지 태그",
            !settings.AutoTagImages ? "꺼짐" : failed > 0 ? $"{waiting}개 대기 · {failed}개 실패" : waiting > 0 ? $"{waiting}개 대기 중" : "모두 완료");
        Row(ClipboardRows, "ok", "빠른 패널", ClipboardSettingsPage.FormatHotkey(settings));
        SwitchRows.Children.Clear();
        Row(SwitchRows, windows.Running ? "ok" : windows.Error is not null ? "bad" : "", "단축키",
            windows.Running ? $"준비됨 · {windows.Slots.Count} / 8 등록" : windows.Error ?? "꺼짐");
        BridgeRows.Children.Clear();
        Row(BridgeRows, bridge.IsRunning ? "ok" : "", "로컬 API", bridge.StateSummary);

        InstallTitle.Text = installed ? "설치됨" : "설치 전";
        InstallDetail.Text = !installed ? "사운드에서 장치를 확인하고 설치합니다."
            : $"{DeviceName(snapshot?.Outputs, snapshot?.RenderId)}, {DeviceName(snapshot?.Inputs, snapshot?.CaptureId)}에 등록 · {(AudioController.NpuInstalled ? "NPU 반향 제거" : "CPU 반향 제거")}";
        RestoreButton.IsEnabled = installed && !audio.Busy;
        string recovery = audio.RecoveryStatus;
        RecoveryState.Text = !installed ? "설치 전" : recovery.Length == 0 ? "정상" : recovery.StartsWith("Multiple matching") ? "보류" : recovery.StartsWith("Device recovery needs attention") ? "확인 필요" : "정상";
        RecoveryDot.Fill = Resource(Tone(!installed ? "" : RecoveryState.Text == "정상" ? "ok" : "warn"));
        _ = LoadLogAsync();
    }
    private static string DeviceName(AudioDevice[]? devices, string? id) => devices?.FirstOrDefault(x => x.Id == id)?.Name ?? "선택한 장치";
    private async Task LoadLogAsync()
    {
        if (audio is null) return;
        string text;
        try { text = await audio.ReadEventsAsync(); }
        catch (IOException) { return; }
        if (text == logSignature) return;
        logSignature = text;
        LogRows.Children.Clear();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse().Take(6).ToArray();
        if (lines.Length == 0)
        {
            LogRows.Children.Add(new TextBlock { Text = "아직 기록이 없습니다. 오디오 처리 상태가 바뀌면 여기에 남습니다.", Margin = new Thickness(16, 10, 16, 10), FontSize = 13, Foreground = Resource("TextFillColorSecondaryBrush") });
            return;
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i], @"^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}) (.*)$");
            string time = match.Success ? match.Groups[2].Value : "", body = match.Success ? match.Groups[3].Value : lines[i];
            var states = Regex.Matches(body, "\"state\":\"([^\"]*)\"").Select(x => x.Groups[1].Value).ToArray();
            string message = states.Length >= 2 ? $"EQ {states[0]} · 반향 제거 {states[1]}" : body;
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 9, 16, 9), BorderBrush = Resource("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = time, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Foreground = Resource("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            var kind = new TextBlock { Text = "오디오", FontSize = 13, Foreground = Resource("TextFillColorSecondaryBrush") }; Grid.SetColumn(kind, 1); row.Children.Add(kind);
            var detail = new TextBlock { Text = message, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(detail, 2); row.Children.Add(detail);
            ToolTipService.SetToolTip(detail, message);
            LogRows.Children.Add(row);
        }
    }
    private void CardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 620;
        SecondColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(AudioCard, narrow ? 0 : 0, narrow ? 0 : 0); Place(ClipboardCard, narrow ? 1 : 0, narrow ? 0 : 1);
        Place(SwitchCard, narrow ? 2 : 1, 0); Place(BridgeCard, narrow ? 3 : 1, narrow ? 0 : 1);
    }
    private static void Place(FrameworkElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); }
    private void RefreshClick(object sender, RoutedEventArgs e) { logSignature = null; _ = audio?.RefreshAsync(); Refresh(); }
    internal void Shown() { logSignature = null; Refresh(); }
    private void OpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AudioController.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AudioController.DataDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception error) { MessageBar.Title = "폴더를 열지 못했습니다"; MessageBar.Message = error.Message; MessageBar.Severity = InfoBarSeverity.Error; MessageBar.IsOpen = true; }
    }
    private async void Restore(object sender, RoutedEventArgs e)
    {
        if (audio is null || audio.Busy) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "원래 오디오 설정으로 복원할까요?",
            Content = "Personal Tools 효과 등록을 제거하고 설치 전 장치 설정과 오디오 보호 설정을 되돌립니다. 가져온 필터는 그대로 둡니다.",
            PrimaryButtonText = "복원", CloseButtonText = "취소", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await audio.RunInstallerAsync(true, "", "", false);
            MessageBar.Title = "원래 설정으로 복원했습니다"; MessageBar.Message = "PC를 다시 시작하고 재생·녹음 앱을 다시 열어 주세요."; MessageBar.Severity = InfoBarSeverity.Success; MessageBar.IsOpen = true;
        }
        catch (Exception error) { MessageBar.Title = "복원하지 못했습니다"; MessageBar.Message = error.Message; MessageBar.Severity = InfoBarSeverity.Error; MessageBar.IsOpen = true; }
    }
}
