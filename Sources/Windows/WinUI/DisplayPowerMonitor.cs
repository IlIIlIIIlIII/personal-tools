using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PersonalTools;

internal sealed class DisplayPowerMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private DisplayPowerSnapshot snapshot = new([]);
    public DisplayPowerSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal static string BackgroundResultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PersonalTools", "DisplayPower", "result.json");
    public void StartAutomatic()
    {
        if (stop.IsCancellationRequested || worker is { IsCompleted: false }) return;
        if (!Directory.Exists(Path.GetDirectoryName(BackgroundResultPath))) return;
        Volatile.Write(ref snapshot, new([], Running: true));
        worker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<BackgroundResult>(await File.ReadAllTextAsync(BackgroundResultPath, stop.Token))
                        ?? throw new IOException("자동 조회 결과가 비어 있습니다.");
                    if (DateTimeOffset.UtcNow - result.CheckedAt > TimeSpan.FromSeconds(30))
                        throw new IOException("관리자 자동 조회가 중단되었습니다. 예약 작업 PersonalTools DisplayPower를 확인하세요.");
                    if (result.ExitCode != 0 || result.Error is not null)
                        throw new IOException(result.Error ?? $"Windows 전원 요청 조회 실패 (코드 {result.ExitCode})");
                    Volatile.Write(ref snapshot, new(DisplayPowerParser.Parse(result.Output), result.CheckedAt, Running: true));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e) { Volatile.Write(ref snapshot, new([], Error: e.Message, Running: true)); }
                try { await Task.Delay(2000, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            }
        });
    }
    private sealed record BackgroundResult(int ExitCode, string Output, string? Error, DateTimeOffset CheckedAt);
    public void Start()
    {
        if (stop.IsCancellationRequested || worker is { IsCompleted: false }) return;
        Volatile.Write(ref snapshot, new([], Running: true));
        worker = Task.Run(RunAsync);
    }
    private sealed record ProbeResult(int ExitCode, string Output, string? Error);
    private async Task RunAsync()
    {
        try
        {
            string pipeName = "PersonalTools.DisplayPower." + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            string script = Path.Combine(AppContext.BaseDirectory, "DisplayPowerProbe.ps1");
            if (!File.Exists(script)) throw new IOException("화면 꺼짐 조회 도구가 없습니다. 앱 패키지를 다시 설치하세요.");
            using var parent = Process.GetCurrentProcess();
            // A fixed, read-only helper: no command or executable supplied over IPC.
            using var helper = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -PipeName {pipeName} -ParentId {parent.Id} -ParentStartTicks {parent.StartTime.ToUniversalTime().Ticks}")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })
                ?? throw new IOException("조회 도구를 시작하지 못했습니다.");
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(30));
                await pipe.WaitForConnectionAsync(connect.Token);
            }
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientId) || clientId != helper.Id)
                throw new IOException("조회 도구의 연결을 확인하지 못했습니다.");
            while (!stop.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var result = await HdrWire.ReadAsync<ProbeResult>(pipe, deadline.Token);
                if (result.ExitCode != 0 || result.Error is not null)
                    throw new IOException(result.Error ?? $"Windows 전원 요청 조회 실패 (코드 {result.ExitCode}): {result.Output.Trim()}");
                var requests = DisplayPowerParser.Parse(result.Output);
                Volatile.Write(ref snapshot, new(requests, DateTimeOffset.Now, Running: true));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e)
        {
            string error = e is Win32Exception { NativeErrorCode: 1223 } ? "관리자 승인이 취소되었습니다. 조회 시작을 눌러 다시 시도하세요."
                : e is OperationCanceledException ? "조회 도구 응답 시간이 초과되었습니다. 조회 시작을 눌러 다시 시도하세요." : e.Message;
            // Discard old requests on failure: stale data must not look like current evidence.
            Volatile.Write(ref snapshot, new([], Error: error));
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (worker is not null) await worker;
        stop.Dispose();
    }
}
