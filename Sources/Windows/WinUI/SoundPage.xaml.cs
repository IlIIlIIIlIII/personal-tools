using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PersonalTools;

public sealed partial class SoundPage : UserControl
{
    private AudioController? audio;
    private Window? owner;
    private AudioSnapshot? shown;
    private bool updating;
    public SoundPage() { InitializeComponent(); }
    internal void Configure(AudioController controller, Window window)
    {
        audio = controller; owner = window;
        audio.Updated += Refresh;
        Refresh();
    }
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    private static void Pill(Microsoft.UI.Xaml.Shapes.Ellipse dot, TextBlock text, string label, string tone)
    {
        text.Text = label;
        dot.Fill = Resource(tone switch { "ok" => "SystemFillColorSuccessBrush", "warn" => "SystemFillColorCautionBrush", "bad" => "SystemFillColorCriticalBrush", _ => "TextFillColorTertiaryBrush" });
    }
    private void Refresh()
    {
        if (audio is null) return;
        var next = audio.Current;
        bool installed = AudioController.Installed, busy = audio.Busy;
        if (audio.Error is { } error && next is null)
        {
            Pill(EqDot, EqPill, "EQ 확인 불가", "bad");
            if (audio.BridgeMissing)
            {
                Pill(AecDot, AecPill, "스피커 참조 수집 안 됨", "bad");
                ReferenceText.Text = "수집 안 됨 · PersonalToolsBridge.dll 없음";
                CaptureWarning.Severity = InfoBarSeverity.Error;
                CaptureWarning.Title = "스피커 참조 수집 안 됨";
                CaptureWarning.Message = error;
                CaptureWarning.IsOpen = true;
                return;
            }
            ShowMessage("오디오 상태를 읽지 못했습니다", error, InfoBarSeverity.Error);
            Pill(AecDot, AecPill, "반향 제거 확인 불가", "bad");
            return;
        }
        if (next is null) return;
        updating = true;
        try
        {
            UpdateDevices(OutputDevice, next.Outputs, next.RenderId, "SMSL", shown?.Outputs);
            UpdateDevices(InputDevice, next.Inputs, next.CaptureId, "In 1-2", shown?.Inputs);
            OutputDevice.IsEnabled = InputDevice.IsEnabled = !installed && !busy;
            string recovery = audio.RecoveryStatus;
            DeviceHint.Text = !installed ? "Windows 기본 재생 장치와 통화 앱도 같은 장치로 맞춰 주세요."
                : recovery.StartsWith("Multiple matching") ? "같은 하드웨어의 출력이 여러 개라 자동 연결을 보류했습니다. 사용할 장치만 연결해 주세요."
                : recovery.StartsWith("Device recovery needs attention") ? "장치 자동 복구를 완료하지 못했습니다. 진단에서 기록을 확인해 주세요."
                : "장치를 바꾸려면 진단에서 원래 설정으로 복원한 뒤 다시 설치합니다.";
            EqSwitch.IsOn = next.EqEnabled; AecSwitch.IsOn = next.AecEnabled;
            EqSwitch.IsEnabled = AecSwitch.IsEnabled = ImportLeft.IsEnabled = ImportRight.IsEnabled = next.Ready && installed && !busy;
            UpdateProfile(LeftSummary, LeftFilters, next.Left, shown?.Left, next.Ready ? "L.txt" : "초기 필터 L.txt");
            UpdateProfile(RightSummary, RightFilters, next.Right, shown?.Right, next.Ready ? "R.txt" : "초기 필터 R.txt");
            SetupInfo.IsOpen = !installed;
            InstallButton.IsEnabled = LocalUnsignedOption.IsEnabled = !busy;
            if (!installed && !(next.Left.Valid && next.Right.Valid))
                SetupInfo.Message = "초기 REW 필터를 찾지 못했습니다. 패키지의 EQ 폴더에 L.txt와 R.txt가 있는지 확인해 주세요.";

            var render = next.Render; var capture = next.Capture;
            bool npu = AudioController.NpuInstalled || (!installed && AudioController.NpuPackaged);
            if (!installed) Pill(EqDot, EqPill, "EQ · 설치 전", "");
            else if (!next.EqEnabled) Pill(EqDot, EqPill, "EQ 꺼짐", "");
            else if (render.Error != 0) Pill(EqDot, EqPill, $"EQ 오류 {render.Error}", "bad");
            else if (render.Active && render.Enabled) Pill(EqDot, EqPill, "EQ 처리 중", "ok");
            else Pill(EqDot, EqPill, "EQ 확인 대기", "warn");

            bool verified = next.AecEnabled && capture.Active && capture.Enabled && capture.Error == 0 && next.Reference;
            if (!installed) Pill(AecDot, AecPill, "반향 제거 · 설치 전", "");
            else if (!next.AecEnabled) Pill(AecDot, AecPill, "반향 제거 꺼짐", "");
            else if (capture.NeuralState == -2) Pill(AecDot, AecPill, "NPU 처리 대기", "warn");
            else if (capture.NeuralState < 0) Pill(AecDot, AecPill, "NPU 복구 중", "warn");
            else if (verified) Pill(AecDot, AecPill, "반향 제거 처리 중", "ok");
            else Pill(AecDot, AecPill, "반향 제거 확인 대기", "warn");

            string device = next.NpuDevice.Length > 0 ? next.NpuDevice : "NPU를 찾지 못했습니다";
            NpuText.Text = npu ? $"{device} · DTLN-AEC 256" : "CPU 신호 처리 (NPU 패키지 없음)";
            if (!npu) Pill(NpuDot, NpuState, "NPU 미사용", "warn");
            else if (!installed || !next.AecEnabled) Pill(NpuDot, NpuState, "대기", "");
            else if (!capture.Active) Pill(NpuDot, NpuState, "마이크 사용 대기", "");
            else Pill(NpuDot, NpuState, capture.NeuralState switch { 2 => "처리 중", 1 => "모델 준비 중", -2 => "처리 프로그램 꺼짐", < 0 => "복구 중", _ => "대기" },
                capture.NeuralState switch { 2 => "ok", < 0 => "warn", _ => "" });
            string output = next.Outputs.FirstOrDefault(x => x.Id == next.RenderId)?.Name ?? "선택한 스피커";
            ReferenceText.Text = !installed ? "설치 후 확인합니다" : capture.Active && next.Reference ? $"{output} · 신호 확인" : $"{output} · 소리가 재생되면 확인합니다";
            LatencyText.Text = npu ? "85.33 ms (NPU 처리 여유 포함)" : "21.33 ms";
            NpuHealth.Text = !installed || !capture.Active ? "마이크를 쓰는 앱이 열리면 표시됩니다"
                : capture.NeuralState == -2 ? $"NPU 처리 프로그램(PersonalToolsNpuHost)이 실행 중이 아닙니다{(audio.NpuHostError is { } hostError ? " · " + hostError : "")} · 자동으로 다시 시작하며 그동안 원음을 전달합니다"
                : capture.NeuralState < 0 ? $"{capture.FailureText} · 자동으로 다시 시도합니다 ({capture.NeuralRestarts}회째) · 그동안 원음을 전달합니다"
                : capture.NeuralRestarts > 0 ? $"정상 · 이 스트림에서 {capture.NeuralRestarts}회 자동 복구됨"
                : $"정상 · 처리한 블록 {capture.NeuralBlocks:N0}개";

            CaptureWarning.IsOpen = installed && next.AecEnabled && (next.Clipping || (capture.Active && !verified && capture.NeuralState >= 0));
            CaptureWarning.Title = next.Clipping ? "마이크 입력이 너무 큽니다" : "반향 제거를 확인하지 못했습니다";
            CaptureWarning.Message = next.Clipping ? "MOTU의 입력 게인을 낮춰 주세요. 잘린 소리는 되돌릴 수 없습니다."
                : capture.Error != 0 && capture.NeuralState >= 0 ? $"마이크 처리 오류 {capture.Error}. 진단의 최근 기록을 확인해 주세요."
                : !capture.Enabled ? "마이크 효과가 우회되고 있습니다. Windows 입력 장치의 오디오 향상 기능과 녹음 앱 설정을 확인해 주세요."
                : "스피커 참조 신호가 아직 없습니다. 선택한 스피커로 소리를 재생하고 Personal Tools를 트레이에 유지해 주세요.";
            shown = next;
        }
        finally { updating = false; }
    }
    private static void UpdateProfile(TextBlock summary, ItemsControl filters, Profile next, Profile? previous, string file)
    {
        summary.Text = next.Valid ? $"{file} · {next.Summary}" : next.Summary;
        ToolTipService.SetToolTip(summary, summary.Text);
        if (previous is not null && next.Valid == previous.Valid && next.Filters.SequenceEqual(previous.Filters)) return;
        filters.ItemsSource = next.Filters;
    }
    private static void UpdateDevices(ComboBox combo, AudioDevice[] devices, string configured, string preferred, AudioDevice[]? previous)
    {
        string? selected = (combo.SelectedItem as AudioDevice)?.Id;
        if (previous is not null && devices.SequenceEqual(previous) && (string.IsNullOrEmpty(configured) || selected == configured)) return;
        var list = devices.ToList();
        string target = !string.IsNullOrEmpty(configured) ? configured : selected ?? "";
        if (!string.IsNullOrEmpty(target) && list.All(d => d.Id != target)) list.Add(new AudioDevice(target, "선택한 장치 연결 대기"));
        combo.ItemsSource = list;
        combo.SelectedItem = list.FirstOrDefault(d => d.Id == target) ?? list.FirstOrDefault(d => d.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
    }
    private void ChannelsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 520;
        Grid.SetColumn(RightProfile, narrow ? 0 : 1);
        Grid.SetRow(RightProfile, narrow ? 1 : 0);
        RightColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }
    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Title = title; MessageBar.Message = message; MessageBar.Severity = severity; MessageBar.IsOpen = true;
    }
    private async Task ExplainAsync(string title, string message) =>
        await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "확인", DefaultButton = ContentDialogButton.Close }.ShowAsync();
    private async void ToggleEffect(object sender, RoutedEventArgs e)
    {
        if (updating || audio?.Current?.Ready != true || audio.Busy) return;
        try { await audio.EnableAsync(EqSwitch.IsOn, AecSwitch.IsOn); }
        catch (Exception error) { ShowMessage("설정을 저장하지 못했습니다", error.Message, InfoBarSeverity.Error); }
    }
    private async void ImportFilter(object sender, RoutedEventArgs e)
    {
        if (audio?.Current?.Ready != true || audio.Busy || owner is null) return;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            int channel = int.Parse((string)((Button)sender).Tag);
            await audio.ImportAsync(channel, file.Path);
            ShowMessage("필터를 가져왔습니다", channel == 0 ? "왼쪽 스피커에 적용했습니다." : "오른쪽 스피커에 적용했습니다.", InfoBarSeverity.Success);
        }
        catch (Exception error) { ShowMessage("필터를 가져오지 못했습니다", error.Message, InfoBarSeverity.Error); }
    }
    private async void Install(object sender, RoutedEventArgs e)
    {
        if (audio is null || audio.Busy) return;
        if (OutputDevice.SelectedItem is not AudioDevice output || InputDevice.SelectedItem is not AudioDevice input)
        { await ExplainAsync("오디오 장치를 선택해 주세요", "스피커 출력과 마이크 입력을 선택한 뒤 다시 시도해 주세요."); return; }
        if (audio.Current?.Left.Valid != true || audio.Current.Right.Valid != true)
        { await ExplainAsync("초기 EQ 파일이 필요합니다", "패키지의 EQ 폴더나 E:\\Speaker에 유효한 REW Configurable_PEQ 파일(L.txt, R.txt)을 준비해 주세요. 오디오 설정은 바뀌지 않았습니다."); return; }
        try
        {
            await audio.RunInstallerAsync(false, output.Id, input.Id, LocalUnsignedOption.IsChecked == true);
            await ExplainAsync("효과 등록을 완료했습니다", "PC를 다시 시작하거나 Windows Audio를 다시 시작한 뒤 재생·녹음 앱을 다시 열어 주세요. 스피커로 소리를 재생하면서 마이크를 쓰면 이 화면에서 NPU 처리와 스피커 참조를 확인할 수 있습니다.");
        }
        catch (Exception error) { await ExplainAsync("설치를 완료하지 못했습니다", error.Message); }
    }
}
