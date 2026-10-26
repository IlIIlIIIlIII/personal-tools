using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace PersonalTools;

// Polls the audio bridge once a second for both the sound and diagnostics pages,
// and runs the elevated installer. Snapshots are delivered on the UI thread.
internal sealed class AudioController : IDisposable
{
    private readonly AudioService? audio;
    private readonly NpuHostSupervisor npuHost = new();
    private readonly DispatcherQueueTimer timer;
    private bool refreshing, disposed;
    public AudioSnapshot? Current { get; private set; }
    public string? Error { get; private set; }
    public bool Busy { get; private set; }
    // No bridge means no process captures the speaker loopback, so the APO cancels no echo at all.
    public bool BridgeMissing { get; }
    public string? BridgeFallback => AudioService.BridgeFallback;
    public event Action? Updated;
    public AudioController(DispatcherQueue dispatcher)
    {
        try { audio = new AudioService(); }
        catch (DllNotFoundException)
        {
            BridgeMissing = true;
            Error = "PersonalToolsBridge.dll을 불러오지 못해 스피커 참조를 수집하지 않습니다. 이 상태에서는 마이크 반향 제거가 동작하지 않습니다. " +
                "설치된 Personal Tools를 실행하거나, 개발 빌드라면 build\\windows-native를 빌드한 뒤 UI를 다시 빌드해 주세요.";
        }
        catch (Exception error) { Error = error.Message; }
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += async (_, _) => { CheckMicrophoneRecovery(); await RefreshAsync(); };
    }
    private string? microphoneRecovery;
    public event Action<MicrophoneRecoveryNotice>? MicrophoneRecoveryChanged;
    // The first read only remembers the line, so restarting the app does not repeat an old notice.
    private void CheckMicrophoneRecovery()
    {
        string status;
        try { var path = Path.Combine(InstallDirectory, "microphone-recovery-status.txt"); status = File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        if (microphoneRecovery is null || status == microphoneRecovery) { microphoneRecovery = status; return; }
        microphoneRecovery = status;
        if (MicrophoneRecovery.Describe(status) is { } notice) MicrophoneRecoveryChanged?.Invoke(notice);
    }
    private static string AudioPath(Environment.SpecialFolder folder, string marker)
    {
        var root = Environment.GetFolderPath(folder);
        var current = Path.Combine(root, "PersonalTools");
        var legacy = Path.Combine(root, "SoundControl");
        return !File.Exists(Path.Combine(current, marker)) && File.Exists(Path.Combine(legacy, marker)) ? legacy : current;
    }
    public static string PackageDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static string DataDirectory => AudioPath(Environment.SpecialFolder.CommonApplicationData, "state-v1.bin");
    public static string InstallDirectory => AudioPath(Environment.SpecialFolder.ProgramFiles, "installation-backup.clixml");
    public static string BackupPath => Path.Combine(InstallDirectory, "installation-backup.clixml");
    public static bool Installed => File.Exists(BackupPath);
    // The NPU payload is required when installed with it; the APO never silently drops to DSP.
    public static bool NpuInstalled => File.Exists(Path.Combine(InstallDirectory, "npu", "enabled.flag"));
    public static bool NpuPackaged => File.Exists(Path.Combine(PackageDirectory, "npu", "enabled.flag"));
    public string RecoveryStatus
    {
        get
        {
            try { var path = Path.Combine(InstallDirectory, "device-recovery-status.txt"); return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (IOException) { return ""; }
        }
    }
    public string? NpuHostError => npuHost.Error;
    public bool NpuHostRunning => npuHost.Running;
    public void Start() { npuHost.Start(); _ = RefreshAsync(); timer.Start(); }
    public async Task RefreshAsync()
    {
        if (refreshing || disposed || audio is null) return;
        refreshing = true;
        try { Current = await audio.ReadAsync(); Error = null; }
        catch (Exception error) { Error = error.Message; }
        finally { refreshing = false; }
        if (!disposed) Updated?.Invoke();
    }
    private async Task RunAsync(Func<AudioService, Task> action)
    {
        if (audio is null) throw new InvalidOperationException(Error ?? "오디오 설정 서비스를 시작하지 못했습니다.");
        Busy = true; Updated?.Invoke();
        try { await action(audio); }
        finally { Busy = false; await RefreshAsync(); }
    }
    public Task EnableAsync(bool eq, bool aec) => RunAsync(a => a.EnableAsync(eq, aec));
    public Task ImportAsync(int channel, string path) => RunAsync(a => a.ImportAsync(channel, path));
    public async Task<string> ReadEventsAsync()
    {
        string path = Path.Combine(DataDirectory, "events.txt");
        string text = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        return text;
    }
    // A non-elevated preflight reports signature/endpoint problems first; only then is UAC requested.
    public async Task RunInstallerAsync(bool remove, string render, string capture, bool localUnsigned)
    {
        Busy = true; Updated?.Invoke();
        try
        {
            string script = remove && Installed ? Path.Combine(InstallDirectory, "uninstall-windows.ps1")
                : Path.Combine(PackageDirectory, remove ? "uninstall-windows.ps1" : "install-windows.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("설치 스크립트가 없습니다. 전체 패키지에서 앱을 실행해 주세요.", script);
            if (!remove)
            {
                var check = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-RenderId", render, "-CaptureId", capture, "-CheckOnly" }) check.ArgumentList.Add(arg);
                if (localUnsigned) check.ArgumentList.Add("-LocalUnsigned");
                using var preflight = Process.Start(check)!;
                var stdout = preflight.StandardOutput.ReadToEndAsync();
                var stderr = preflight.StandardError.ReadToEndAsync();
                await preflight.WaitForExitAsync();
                await stdout;
                string error = await stderr;
                if (preflight.ExitCode != 0) throw new InvalidOperationException(ExplainPreflight(error.Trim(), localUnsigned));
            }
            string arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
            if (!remove) arguments += $" -RenderId \"{render}\" -CaptureId \"{capture}\"";
            if (localUnsigned) arguments += " -LocalUnsigned";
            Process process;
            try { process = Process.Start(new ProcessStartInfo("powershell.exe", arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!; }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
            { throw new InvalidOperationException("관리자 승인이 취소되어 아무것도 바꾸지 않았습니다. 다시 시도하고 사용자 계정 컨트롤 창에서 '예'를 눌러 주세요."); }
            using var _ = process;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"설치 도구가 완료되지 않았습니다 (코드 {process.ExitCode}).");
        }
        finally { Busy = false; await RefreshAsync(); }
    }
    // The installer reports in English; the common blockers get a Korean explanation with the next step.
    internal static string ExplainPreflight(string error, bool localUnsigned) =>
        error.Contains("signature is NotSigned", StringComparison.Ordinal) && !localUnsigned
            ? "이 PC에서 직접 빌드한 오디오 모듈은 서명되어 있지 않아 설치가 막혔습니다. '이 PC에서 직접 빌드한 미서명 오디오 모듈 사용'에 체크한 뒤 다시 설치해 주세요. 오디오 설정은 바뀌지 않았습니다."
            : error.Contains("Existing effects on", StringComparison.Ordinal)
            ? "선택한 장치에 다른 오디오 효과가 이미 등록되어 있어 설치하지 않았습니다. 오디오 설정은 바뀌지 않았습니다.\n\n" + error
            : error.Contains("Already installed", StringComparison.Ordinal)
            ? "이미 설치되어 있습니다. 장치를 바꾸려면 진단에서 원래 설정으로 복원한 뒤 다시 설치해 주세요."
            : error;
    public void Dispose() { disposed = true; timer.Stop(); npuHost.Dispose(); audio?.Dispose(); }
}
