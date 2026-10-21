using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PersonalTools;

public sealed partial class HdrPage : UserControl
{
    private HdrController? controller;
    private Window? owner;
    private string? rendered;
    private string? renderedPower;
    public HdrPage() { InitializeComponent(); }
    internal void Configure(HdrController service, Window window)
    {
        controller = service; owner = window;
        service.Changed += snapshot => DispatcherQueue.TryEnqueue(() => Render(snapshot));
        Render(service.Snapshot);
    }
    private void Error(Exception e) { Status.Message = e.Message; Status.IsOpen = true; }
    private void Render(HdrSnapshot snapshot)
    {
        var power = snapshot.DisplayPower ?? new DisplayPowerSnapshot([]);
        string powerJson = JsonSerializer.Serialize(power);
        if (renderedPower != powerJson)
        {
            renderedPower = powerJson;
            PowerSummary.Text = power.Summary;
            PowerDetail.Text = power.Error ?? (power.CheckedAt is null
                ? "조회를 시작하면 화면을 켜 두도록 요청한 프로세스·서비스·드라이버와 사유를 표시합니다."
                : string.Join("\n\n", power.Requests.Select(r => $"[{r.Kind}] {r.Name}\n{r.Reason}"))
                    + $"\n마지막 확인: {power.CheckedAt.Value.ToLocalTime():HH:mm:ss}");
            PowerStart.IsEnabled = !power.Running;
            PowerStart.Content = power.Running ? "조회 중 · 5초마다 자동 갱신" : "관리자 권한으로 조회 시작";
        }
        string serialized = JsonSerializer.Serialize(snapshot with { DisplayPower = null });
        if (rendered == serialized) return;
        rendered = serialized;
        Status.Message = snapshot.Error ?? snapshot.Warning ?? ""; Status.IsOpen = snapshot.Error is not null || snapshot.Warning is not null;
        Monitors.Children.Clear(); Programs.Children.Clear();
        if (snapshot.Monitors.Length == 0) Monitors.Children.Add(new TextBlock { Text = "연결된 모니터를 확인하고 있습니다." });
        foreach (var monitor in snapshot.Monitors)
        {
            var toggle = new ToggleSwitch { Header = monitor.Name + (monitor.Primary ? " · 주 모니터" : ""), IsOn = monitor.Enabled,
                IsEnabled = monitor.Supported && monitor.Error is null, OnContent = "HDR 켜짐", OffContent = monitor.Supported ? "HDR 꺼짐" : "HDR 미지원" };
            toggle.Toggled += async (_, _) =>
            {
                bool value = toggle.IsOn;
                toggle.IsEnabled = false;
                try { await Task.Run(() => controller!.RequestAsync(new("set", monitor.Id, value))); }
                catch (Exception e) { Error(e); }
                finally { toggle.IsEnabled = true; }
            };
            Monitors.Children.Add(new Border { Style = (Style)Application.Current.Resources["Card"], Child = toggle });
        }
        if (snapshot.Programs.Length == 0) Programs.Children.Add(new TextBlock { Text = "등록된 프로그램이 없습니다." });
        foreach (var item in snapshot.Programs)
        {
            var program = item.Program;
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = program.Name + (item.Running ? " · 실행 중" : " · 대기"), FontSize = 16 });
            panel.Children.Add(new TextBlock { Text = program.Path, TextWrapping = TextWrapping.Wrap, Opacity = .7 });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var enabled = new ToggleSwitch { IsOn = program.Enabled, OnContent = "자동 적용", OffContent = "사용 안 함" };
            enabled.Toggled += async (_, _) =>
            {
                bool value = enabled.IsOn;
                try { await Task.Run(() => controller!.SaveProgramAsync(program with { Enabled = value })); }
                catch (Exception e) { Error(e); }
            };
            var edit = new Button { Content = "편집" };
            edit.Click += async (_, _) => await EditAsync(program);
            var remove = new Button { Content = "삭제" };
            remove.Click += async (_, _) =>
            {
                try { await Task.Run(() => controller!.SaveProgramAsync(program, true)); }
                catch (Exception e) { Error(e); }
            };
            buttons.Children.Add(enabled); buttons.Children.Add(edit); buttons.Children.Add(remove);
            panel.Children.Add(buttons);
            Programs.Children.Add(new Border { Style = (Style)Application.Current.Resources["Card"], Child = panel });
        }
    }
    private void StartPowerQuery(object sender, RoutedEventArgs e)
    {
        controller?.StartDisplayPower();
        PowerStart.IsEnabled = false;
        PowerSummary.Text = "관리자 승인 및 조회 대기 중";
    }
    private async void AddFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner!));
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await EditAsync(new(Guid.NewGuid(), file.DisplayName, file.Path));
        }
        catch (Exception error) { Error(error); }
    }
    private async void AddRunning(object sender, RoutedEventArgs e)
    {
        try
        {
            var processes = await Task.Run(HdrController.ListProcesses);
            var list = new ListView { ItemsSource = processes, DisplayMemberPath = "Path", SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "실제 게임 프로세스 선택", Content = list, PrimaryButtonText = "선택", CloseButtonText = "취소" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is HdrProcess process)
                await EditAsync(new(Guid.NewGuid(), Path.GetFileNameWithoutExtension(process.Path), process.Path));
        }
        catch (Exception error) { Error(error); }
    }
    private async Task EditAsync(HdrProgram program)
    {
        try
        {
            var name = new TextBox { Header = "표시 이름", Text = program.Name };
            var path = new TextBox { Header = "실행 파일 전체 경로", Text = program.Path };
            var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(name); panel.Children.Add(path);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "자동 HDR 프로그램", Content = panel, PrimaryButtonText = "저장", CloseButtonText = "취소", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var edited = program with { Name = name.Text, Path = path.Text };
                await Task.Run(() => controller!.SaveProgramAsync(edited));
            }
        }
        catch (Exception e) { Error(e); }
    }
}
