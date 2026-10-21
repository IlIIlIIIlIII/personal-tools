using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PersonalTools;

// Starts PersonalToolsNpuHost.exe (NPU echo cancellation outside audiodg) and
// restarts it with backoff if it exits. A kill-on-close job ties it to this app.
internal sealed class NpuHostSupervisor : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly nint job;
    private Process? process;
    private Task? loop;
    public string? Error { get; private set; }
    public bool Running { get { try { return process is { HasExited: false }; } catch (InvalidOperationException) { return false; } } }
    public static string HostPath => Path.Combine(AudioController.InstallDirectory, "PersonalToolsNpuHost.exe");
    public NpuHostSupervisor()
    {
        job = CreateJobObject(0, null);
        if (job == 0) return;
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        SetInformationJobObject(job, 9, ref info, Marshal.SizeOf<ExtendedLimit>());
    }
    public void Start() => loop ??= Task.Run(RunAsync);
    private async Task RunAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                if (!AudioController.Installed || !AudioController.NpuInstalled || !File.Exists(HostPath))
                { Error = null; await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token); continue; }
                var started = Process.Start(new ProcessStartInfo(HostPath, $"--parent {Environment.ProcessId}")
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AudioController.InstallDirectory })
                    ?? throw new InvalidOperationException("NPU 처리 프로그램을 시작하지 못했습니다.");
                if (job != 0) AssignProcessToJobObject(job, started.Handle);
                process = started; Error = null;
                var at = DateTime.UtcNow;
                await started.WaitForExitAsync(lifetime.Token);
                Error = $"NPU 처리 프로그램이 종료되었습니다 (코드 {started.ExitCode})";
                if (DateTime.UtcNow - at > TimeSpan.FromMinutes(1)) delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error) { Error = error.Message; }
            try { await Task.Delay(delay, lifetime.Token); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }
    public void Dispose()
    {
        lifetime.Cancel();
        try { if (Running) process!.Kill(); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (job != 0) CloseHandle(job);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimit info, int size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
