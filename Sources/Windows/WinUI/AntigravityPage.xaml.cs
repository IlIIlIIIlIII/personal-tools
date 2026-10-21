using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PersonalTools;

public sealed partial class AntigravityPage : UserControl
{
    private AntigravityBridge bridge => ((App)Application.Current).Bridge;
    private readonly CancellationTokenSource lifetime = new();
    private BridgeSettings? running;
    private string cliPath;
    private string? lastError;
    private bool busy;
    private Window? owner;
    public event Action? StateChanged;
    public bool IsRunning => bridge.IsRunning;
    public string StateSummary => bridge.IsRunning ? $"실행 중 · 127.0.0.1:{running?.Port}" : lastError is null ? "중지됨" : "확인 필요";
    public AntigravityPage()
    {
        InitializeComponent();
        bridge.Status += message => DispatcherQueue.TryEnqueue(() => { if (!bridge.IsRunning) { StateDetail.Text = message; } });
        BridgeSettings settings;
        try { settings = BridgeSettings.Load(); }
        catch (Exception e) { settings = new(BridgeSettings.DefaultCli); ShowError(e); }
        cliPath = settings.CliPath;
        CliPathText.Text = cliPath;
        ToolTipService.SetToolTip(CliPathText, cliPath);
        PortBox.Value = settings.Port;
        TimeoutBox.Value = settings.TimeoutSeconds;
        ModelBox.ItemsSource = new[] { settings.Model };
        ModelBox.SelectedIndex = 0;
        UpdateKey();
        UpdateControls();
    }
    internal void Configure(Window window) => owner = window;
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    private BridgeSettings Settings()
    {
        if (!double.IsFinite(PortBox.Value) || !double.IsFinite(TimeoutBox.Value) || PortBox.Value != Math.Truncate(PortBox.Value) || TimeoutBox.Value != Math.Truncate(TimeoutBox.Value))
            throw new ArgumentException("포트와 제한 시간은 정수로 입력해 주세요.");
        var settings = new BridgeSettings(cliPath.Trim(), checked((int)PortBox.Value), ModelBox.Text.Trim(), checked((int)TimeoutBox.Value));
        settings.Validate(); return settings;
    }
    private void ShowError(Exception error)
    {
        lastError = error is OperationCanceledException ? "작업 시간이 초과되었거나 취소되었습니다." : error.Message;
        UpdateControls();
    }
    private void UpdateKey()
    {
        string key = bridge.ApiKey;
        KeyText.Text = RevealKey.IsChecked == true ? key : key[..Math.Min(9, key.Length)] + new string('•', 20) + key[^Math.Min(4, key.Length)..];
    }
    private void UpdateControls()
    {
        bool on = bridge.IsRunning, editable = !busy && !on;
        PortBox.IsEnabled = TimeoutBox.IsEnabled = ModelBox.IsEnabled = BrowseButton.IsEnabled = editable;
        CheckButton.IsEnabled = LoginButton.IsEnabled = !busy;
        StartButton.IsEnabled = editable; StopButton.IsEnabled = !busy && on;
        StartButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TestButton.IsEnabled = !busy && on;
        BaseUrl.Text = $"http://127.0.0.1:{(running?.Port ?? (double.IsFinite(PortBox.Value) ? (int)PortBox.Value : 8877))}/v1";
        if (busy) { StateTitle.Text = "처리 중"; StateDetail.Text = "잠시만 기다려 주세요."; }
        else if (on) { StateTitle.Text = "실행 중"; StateDetail.Text = "이 PC 안에서만 접속할 수 있습니다. 다른 앱에 아래 주소와 키를 입력하세요."; }
        else if (lastError is not null) { StateTitle.Text = "확인이 필요합니다"; StateDetail.Text = lastError; }
        else { StateTitle.Text = "중지됨"; StateDetail.Text = "CLI를 확인한 뒤 시작하면 다른 앱에서 연결할 수 있습니다."; }
        StateCircle.Fill = Resource(on ? "SystemFillColorSuccessBrush" : lastError is not null ? "SystemFillColorCautionBrush" : "ControlStrongFillColorDefaultBrush");
        StateGlyph.Glyph = on ? "" : lastError is not null ? "" : "";
        StateChanged?.Invoke();
    }
    private void SetCliState(string text, bool ok)
    {
        CliPill.Visibility = Visibility.Visible; CliState.Text = text;
        CliDot.Fill = Resource(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
    }
    private async void BrowseCli(object sender, RoutedEventArgs e)
    {
        if (owner is null) return;
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            cliPath = file.Path; CliPathText.Text = cliPath; ToolTipService.SetToolTip(CliPathText, cliPath);
            CliPill.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) { ShowError(error); }
    }
    private async void CheckCli(object sender, RoutedEventArgs e)
    {
        busy = true; UpdateControls();
        try
        {
            var settings = Settings();
            var models = await AntigravityBridge.GetModelsAsync(settings, lifetime.Token);
            ModelBox.ItemsSource = models;
            ModelBox.SelectedItem = models.Contains(settings.Model) ? settings.Model : null;
            ModelBox.Text = settings.Model;
            lastError = null;
            SetCliState($"확인됨 · 모델 {models.Length - 1}개", true);
        }
        catch (Exception error) { SetCliState("확인 필요", false); ShowError(error); }
        finally { busy = false; UpdateControls(); }
    }
    private void OpenCli(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = Settings();
            Directory.CreateDirectory(BridgeSettings.DirectoryPath);
            Process.Start(new ProcessStartInfo(settings.CliPath) { UseShellExecute = true, WorkingDirectory = BridgeSettings.DirectoryPath });
        }
        catch (Exception error) { ShowError(error); }
    }
    private async void StartBridge(object sender, RoutedEventArgs e) => await StartBridgeAsync();
    public async Task StartBridgeAsync()
    {
        if (busy || bridge.IsRunning || lifetime.IsCancellationRequested) return;
        busy = true; UpdateControls();
        try
        {
            var settings = Settings();
            settings.Save();
            await bridge.StartAsync(settings, lifetime.Token);
            running = settings; lastError = null;
        }
        catch (Exception error) { ShowError(error); }
        finally { busy = false; UpdateControls(); }
    }
    private async void StopBridge(object sender, RoutedEventArgs e)
    {
        busy = true; UpdateControls();
        try { await bridge.StopAsync(); running = null; }
        catch (Exception error) { ShowError(error); }
        finally { busy = false; UpdateControls(); }
    }
    private void RevealKeyClick(object sender, RoutedEventArgs e) => UpdateKey();
    private void SetClipboard(string text)
    {
        var data = new DataPackage();
        data.SetData(ClipboardController.OwnFormat, "1");
        data.SetText(text);
        Clipboard.SetContent(data);
    }
    private void CopyBaseUrl(object sender, RoutedEventArgs e)
    {
        try { SetClipboard(BaseUrl.Text); StateDetail.Text = "Base URL을 복사했습니다."; }
        catch (Exception error) { ShowError(error); }
    }
    private void CopyConnection(object sender, RoutedEventArgs e)
    {
        try
        {
            int port = running?.Port ?? Settings().Port;
            SetClipboard($"OPENAI_BASE_URL=http://127.0.0.1:{port}/v1\nOPENAI_API_KEY={bridge.ApiKey}");
            StateDetail.Text = "연결 주소와 키를 복사했습니다.";
        }
        catch (Exception error) { ShowError(error); }
    }
    private async void TestBridge(object sender, RoutedEventArgs e)
    {
        if (running is null) return;
        TestButton.IsEnabled = false;
        TestResult.Text = "응답을 기다리는 중…";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(running.TimeoutSeconds + 5) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bridge.ApiKey);
            using var response = await client.PostAsJsonAsync($"http://127.0.0.1:{running.Port}/v1/chat/completions",
                new { model = running.Model, messages = new[] { new { role = "user", content = "안녕하세요. 한 문장으로 짧게 인사해 주세요." } } }, lifetime.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(lifetime.Token));
            TestResult.Text = response.IsSuccessStatusCode
                ? "응답 · " + json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                : "오류 · " + json.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch (Exception error) { TestResult.Text = "오류 · " + (error is OperationCanceledException or TaskCanceledException ? "시간이 초과되었습니다." : error.Message); }
        finally { UpdateControls(); }
    }
    public async Task ShutdownAsync() { lifetime.Cancel(); await bridge.StopAsync(); }
}
