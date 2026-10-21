using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;

namespace PersonalTools;

internal sealed class VllmController : IAsyncDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stop = new();
    private Task? server, launch;
    private DateTime checkedAt;
    private readonly Func<Task<VllmSnapshot>>? probeOverride;
    private readonly Func<CancellationToken, Task>? startOverride;
    private readonly Func<CancellationToken, Task>? stopOverride;
    private readonly string pipeName;
    internal VllmController(Func<Task<VllmSnapshot>>? probe = null, Func<CancellationToken, Task>? start = null, string? pipeName = null, Func<CancellationToken, Task>? stopService = null)
    { probeOverride = probe; startOverride = start; stopOverride = stopService; this.pipeName = pipeName ?? VllmProtocol.PipeName; }
    public VllmSnapshot Snapshot { get; private set; } = new();
    public event Action<VllmSnapshot>? Changed;
    public void Start() => server ??= Task.Run(ServeAsync);
    private void Publish(VllmSnapshot value) { Snapshot = value; Changed?.Invoke(value); }

    private async Task<VllmSnapshot> ProbeAsync()
    {
        if (probeOverride is not null) return await probeOverride();
        try
        {
            using var response = await http.GetAsync(VllmProtocol.Endpoint + "/models", stop.Token);
            if (!response.IsSuccessStatusCode) return new("conflict", "API 응답을 확인해 주세요.", Error: $"HTTP {(int)response.StatusCode}");
            return VllmProtocol.ParseModels(await response.Content.ReadAsStringAsync(stop.Token));
        }
        catch (HttpRequestException) { return new("offline", "연결 안 됨 · 시작할 수 있습니다."); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { return new("offline", "API 응답 대기 시간 초과"); }
        catch (System.Text.Json.JsonException e) { return new("conflict", "모델 응답을 읽지 못했습니다.", Error: e.Message); }
        catch (Exception e) when (!stop.IsCancellationRequested) { return new("conflict", "API 확인 실패", Error: e.Message); }
    }

    public async Task<VllmSnapshot> RequestAsync(string command)
    {
        await gate.WaitAsync(stop.Token);
        try
        {
            if (command is not ("read" or "refresh" or "start" or "stop")) throw new IOException("지원하지 않는 vLLM 명령입니다.");
            if (Snapshot.Busy) return Snapshot;
            if (command != "read" || DateTime.UtcNow - checkedAt > TimeSpan.FromSeconds(5))
            {
                var next = await ProbeAsync();
                if (Snapshot.State == "stopped" && next.State == "offline") next = new("stopped", "중지됨");
                // Keep startup diagnostics visible until retry or successful connection.
                if (Snapshot.Error is not null && next.State == "offline") next = next with { Error = Snapshot.Error, Progress = Snapshot.Progress };
                else if (Snapshot.Ready && next.Ready) next = next with { Progress = Snapshot.Progress };
                Publish(next); checkedAt = DateTime.UtcNow;
            }
            if (command == "start" && Snapshot.CanStart)
            {
                Publish(new("starting", "WSL 연결 중…", Progress: VllmProgress.Begin()));
                launch = Task.Run(LaunchAsync);
            }
            else if (command == "stop" && Snapshot.CanStop)
            {
                Publish(new("stopping", "서비스 중지 중…"));
                launch = Task.Run(StopServiceAsync);
            }
            return Snapshot;
        }
        finally { gate.Release(); }
    }

    private async Task StopServiceAsync()
    {
        VllmSnapshot result;
        try
        {
            if (stopOverride is not null) await stopOverride(stop.Token);
            else
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsl.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-d", "Ubuntu-26.04", "-u", "sunggu", "--", "bash", "-lc",
                    "cd /home/sunggu/Projects/Personal/llm && ./stop.sh && systemctl --user show qwen38.service -p ActiveState --value | grep -qx inactive && systemctl --user show qwen38.service -p MainPID --value | grep -qx 0" }) info.ArgumentList.Add(arg);
                using var process = Process.Start(info) ?? throw new IOException("WSL을 시작하지 못했습니다.");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) { try { process.Kill(); } catch { } throw; }
                string detail = (await output) + (await error);
                if (process.ExitCode != 0) throw new IOException($"서비스 중지 확인 실패 ({process.ExitCode}) " + detail);
            }
            var probe = await ProbeAsync();
            result = probe.State == "offline" ? new("stopped", "중지됨") : probe with { Error = "서비스는 중지했지만 API가 응답합니다. 다른 서버가 실행 중인지 확인하세요." };
        }
        catch (OperationCanceledException) { result = new("offline", "중지 확인이 중단되었습니다.", Error: "새로 고침 후 상태를 확인하거나 중지를 다시 시도하세요."); }
        catch (Exception e) { result = new("offline", "중지하지 못했습니다.", Error: e.Message); }
        await gate.WaitAsync();
        try { Publish(result); checkedAt = DateTime.UtcNow; }
        finally { gate.Release(); }
    }

    private async Task LaunchAsync()
    {
        VllmSnapshot result;
        try
        {
            if (startOverride is not null) await startOverride(stop.Token);
            else
            {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsl.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            // One WSL invocation; all Python, model files and warmup remain in Linux.
            // The streamed observer locks starts and follows only new Linux log bytes.
            foreach (var arg in new[] { "-d", "Ubuntu-26.04", "-u", "sunggu", "--", "bash", "-lc",
                "cd /home/sunggu/Projects/Personal/llm && exec .venv/bin/python -u -" }) info.ArgumentList.Add(arg);
            string observer = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "VllmStart.py"), stop.Token);
            using var process = Process.Start(info) ?? throw new IOException("WSL을 시작하지 못했습니다.");
            var output = ReadProgressAsync(process.StandardOutput);
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(observer);
            process.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(25));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                // Do not stop the systemd model service when the desktop app exits.
                try { process.Kill(); } catch { }
                throw;
            }
            string log = (await output) + "\n" + (await error);
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalTools", "Vllm");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "startup.log"), log);
            if (process.ExitCode != 0) throw new IOException($"시작 스크립트 실패 ({process.ExitCode})\n" + log[^Math.Min(log.Length, 1800)..]);
            }
            result = await ProbeAsync();
            if (!result.Ready) result = result with { Error = "워밍업 이후 Windows에서 모델 API에 연결하지 못했습니다." };
            result = result with { Progress = result.Ready ? Snapshot.Progress?.Move(7, "모든 준비가 끝났습니다.") : Snapshot.Progress };
        }
        catch (OperationCanceledException) { result = new("offline", "시작 확인이 중단되었습니다.", Error: "서버가 계속 준비 중일 수 있습니다. 새로 고침으로 확인하세요.", Progress: Snapshot.Progress); }
        catch (Exception e) { result = new("offline", "시작하지 못했습니다.", Error: e.Message, Progress: Snapshot.Progress); }
        await gate.WaitAsync();
        try { Publish(result); checkedAt = DateTime.UtcNow; }
        finally { gate.Release(); }
    }

    private async Task<string> ReadProgressAsync(StreamReader reader)
    {
        var log = new StringBuilder();
        try
        {
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                log.AppendLine(line);
                if (log.Length > 65536) log.Remove(0, log.Length - 65536);
                await gate.WaitAsync(stop.Token);
                try
                {
                    var progress = Snapshot.Progress?.Observe(line);
                    if (Snapshot.Busy && progress is not null && progress != Snapshot.Progress)
                        Publish(Snapshot with { Progress = progress, Message = progress.Title });
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        return log.ToString();
    }

    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                VllmSnapshot response;
                try { response = await RequestAsync(await HdrWire.ReadAsync<string>(pipe, deadline.Token)); }
                catch (Exception e) { response = Snapshot with { Error = e.Message }; }
                await HdrWire.WriteAsync(pipe, response, deadline.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(250, CancellationToken.None); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (server is not null) await server;
        if (launch is not null) await launch;
        http.Dispose();
    }
}
