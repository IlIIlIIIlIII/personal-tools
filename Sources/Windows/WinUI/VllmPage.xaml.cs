using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Text;

namespace PersonalTools;

public sealed partial class VllmPage : UserControl
{
    private VllmController? controller;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? renderedSteps;
    private VllmSnapshot current = new();
    private bool narrow;
    private bool refreshing;
    public VllmPage()
    {
        InitializeComponent();
        StartProfileText.Text = VllmProtocol.StartProfile;
        timer.Tick += async (_, _) => { if (Visibility == Visibility.Visible) await SendAsync("read"); };
        Loaded += async (_, _) => { timer.Start(); if (Visibility == Visibility.Visible) await SendAsync("read"); };
        Unloaded += (_, _) => timer.Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, async (_, _) => { if (Visibility == Visibility.Visible) await SendAsync("read"); });
    }
    internal void Configure(VllmController service)
    {
        controller = service;
        service.Changed += value => DispatcherQueue.TryEnqueue(() => Render(value));
        Render(service.Snapshot);
    }
    private void Render(VllmSnapshot value)
    {
        current = value;
        StatusText.Text = value.State == "stopping" ? "중지 중" : value.State == "stopped" ? "중지됨" : value.Ready ? "사용 가능" : value.Busy ? "준비 중" : value.Error is not null ? "확인 필요" : "연결 대기";
        StatusDot.Fill = Brush(value.Ready ? "SystemFillColorSuccessBrush" : value.Busy ? "AccentFillColorDefaultBrush" : "TextFillColorSecondaryBrush");
        ContextText.Text = VllmProtocol.Model + (value.Context is int count ? $" · 컨텍스트 {count:N0} 토큰" : "");
        StartButton.IsEnabled = value.CanStart;
        StartLabel.Text = value.State == "starting" ? "시작 중…" : value.Ready ? "실행 중" : "vLLM 시작";
        StopButton.IsEnabled = value.CanStop;
        StopLabel.Text = value.State == "stopping" ? "중지 중…" : "중지";
        Progress.Visibility = value.Busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressHeading.Text = value.Progress?.Title ?? (value.Ready ? "모델이 대화할 준비가 됐습니다" : "시작하면 이 순서로 준비합니다");
        ProgressDetail.Text = value.Progress?.Detail ?? (value.Ready ? "현재 연결은 확인됐습니다. 이전 시작 단계의 기록은 없으며, 다음 시작부터 실시간 진행을 표시합니다." : "모델 로딩과 워밍업에 수 분이 걸릴 수 있습니다. 진행 단계는 실제 서버 로그로 갱신됩니다.");
        ProgressElapsed.Text = "";
        if (value.State is "stopping" or "stopped")
        {
            ProgressHeading.Text = value.State == "stopping" ? "vLLM 서버를 중지하고 있습니다" : "서버가 중지됐습니다";
            ProgressDetail.Text = value.State == "stopping" ? "서비스 종료와 모델 프로세스 해제를 확인합니다." : "vLLM 시작을 누르면 모델을 다시 준비합니다.";
            Progress.IsIndeterminate = true;
        }
        if (value.Progress is { } progress)
        {
            Progress.IsIndeterminate = progress.Percent is null;
            Progress.Value = progress.Percent ?? 0;
            ProgressDetail.Text = progress.Detail;
            ProgressElapsed.Text = value.Busy || value.Ready ? progress.ElapsedText : $"중단 단계: {progress.Title}";
        }
        RenderSteps();
        ErrorBar.Message = value.Error ?? "";
        ErrorBar.IsOpen = value.Error is not null;
        ErrorBar.Visibility = ErrorBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    private Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private void StepsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool next = e.NewSize.Width < 660;
        if (next == narrow && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1) return;
        narrow = next; renderedSteps = null; RenderSteps();
    }
    private void RenderSteps()
    {
        string key = $"{current.Progress?.Step}:{current.State}:{narrow}:{ActualTheme}";
        if (key == renderedSteps) return;
        renderedSteps = key;
        ProgressSteps.Children.Clear(); ProgressSteps.ColumnDefinitions.Clear(); ProgressSteps.RowDefinitions.Clear();
        int step = current.Progress?.Step ?? -1;
        for (int i = 0; i < VllmProgress.Steps.Length; i++)
        {
            if (narrow) ProgressSteps.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            else ProgressSteps.ColumnDefinitions.Add(new ColumnDefinition());
            bool done = step >= 0 && i < step;
            bool active = i == step;
            bool verified = current.Ready && step < 0 && i == 6;
            string status = done ? "완료" : verified ? "연결 확인" : active ? current.Busy ? "진행 중" : "확인 필요" : current.Ready && step < 0 ? "기록 없음" : "대기";
            Brush tone = Brush(done || verified ? "SystemFillColorSuccessBrush" : active ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            var cell = new Grid { Margin = narrow ? new Thickness(0,0,0,12) : new Thickness(0), RowSpacing = 10, ColumnSpacing = 14 };
            Grid.SetRow(cell, narrow ? i : 0); Grid.SetColumn(cell, narrow ? 0 : i);
            if (narrow) { cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) }); cell.ColumnDefinitions.Add(new ColumnDefinition()); cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); }
            else { cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            if (!narrow && i < 6)
            {
                var line = new Border { Height = 2, Background = Brush(done ? "SystemFillColorSuccessBrush" : "DividerStrokeColorDefaultBrush"), Margin = new Thickness(18,0,18,0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Translation = new System.Numerics.Vector3((float)(ProgressSteps.ActualWidth / 14),0,0) };
                cell.Children.Add(line);
            }
            var node = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(active ? 2 : 1), BorderBrush = tone, Background = Brush("LayerFillColorDefaultBrush"), HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Center };
            node.Child = done || verified ? new FontIcon { Glyph = "\uE73E", FontSize = 14, Foreground = tone } : new TextBlock { Text = (i+1).ToString(), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = tone, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(node);
            var title = new TextBlock { Text = VllmProgress.Steps[i], FontSize = 13, FontWeight = active || done || verified ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, TextAlignment = narrow ? TextAlignment.Left : TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = narrow ? new Thickness(0) : new Thickness(4,0,4,0) };
            Grid.SetColumn(title, narrow ? 1 : 0); Grid.SetRow(title, narrow ? 0 : 1); cell.Children.Add(title);
            var caption = new TextBlock { Text = status, FontSize = 12, Foreground = tone, TextAlignment = narrow ? TextAlignment.Right : TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(caption, narrow ? 2 : 0); Grid.SetRow(caption, narrow ? 0 : 2); cell.Children.Add(caption);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, $"{i+1}단계 {VllmProgress.Steps[i]} {status}");
            ProgressSteps.Children.Add(cell);
        }
    }
    private async Task SendAsync(string command)
    {
        if (controller is null || refreshing) return;
        refreshing = true; StartButton.IsEnabled = false;
        try { Render(await controller.RequestAsync(command)); }
        catch (Exception e) { Render(controller.Snapshot with { Error = e.Message }); }
        finally { refreshing = false; }
    }
    private async void StartClick(object sender, RoutedEventArgs e) => await SendAsync("start");
    private async void StopClick(object sender, RoutedEventArgs e) => await SendAsync("stop");
    private async void RefreshClick(object sender, RoutedEventArgs e) => await SendAsync("refresh");
    private void CopyClick(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage(); data.SetText(VllmProtocol.Endpoint); Clipboard.SetContent(data);
    }
}
