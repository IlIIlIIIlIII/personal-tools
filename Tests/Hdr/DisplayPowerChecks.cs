using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using PersonalTools;

internal static class DisplayPowerChecks
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);
    public static void HoldDisplay()
    {
        if (SetThreadExecutionState(0x80000002) == 0) throw new Exception("Display fixture request failed.");
        try { Thread.Sleep(15000); }
        finally { SetThreadExecutionState(0x80000000); }
    }
    private static async Task<HdrSnapshot> ReadInstalledAsync()
    {
        using var pipe = new NamedPipeClientStream(".", HdrWire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(deadline.Token);
        await HdrWire.WriteAsync(pipe, new HdrRequest("read"), deadline.Token);
        return await HdrWire.ReadAsync<HdrSnapshot>(pipe, deadline.Token);
    }
    public static async Task CheckInstalledAsync()
    {
        var initial = (await ReadInstalledAsync()).DisplayPower;
        if (initial?.Running != true || initial.CheckedAt is null || initial.Error is not null)
            throw new Exception("Start and approve display power monitoring in the installed app first.");
        Console.WriteLine($"PASS installed app IPC: {initial.Summary}");
        using var fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--display-power-fixture")
            { UseShellExecute = false, CreateNoWindow = true })!;
        bool found = false;
        DateTimeOffset? refreshedAt = null;
        try
        {
            for (int i = 0; i < 12; i++)
            {
                var current = (await ReadInstalledAsync()).DisplayPower!;
                if (current.Error is not null) throw new Exception(current.Error);
                if (current.Requests.Any(r => r.Name.EndsWith("HdrTests.exe", StringComparison.OrdinalIgnoreCase)))
                { found = true; refreshedAt = current.CheckedAt; break; }
                await Task.Delay(1000);
            }
            if (!found || refreshedAt <= initial.CheckedAt) throw new Exception("New DISPLAY request not detected or stale timestamp.");
            Console.WriteLine("PASS actual test process DISPLAY request detected and timestamp advanced");
            await fixture.WaitForExitAsync();
            for (int i = 0; i < 12; i++)
            {
                var current = (await ReadInstalledAsync()).DisplayPower!;
                if (current.Error is not null) throw new Exception(current.Error);
                if (current.CheckedAt > refreshedAt && !current.Requests.Any(r => r.Name.EndsWith("HdrTests.exe", StringComparison.OrdinalIgnoreCase)))
                { Console.WriteLine("PASS DISPLAY request cleared after test process exit"); return; }
                await Task.Delay(1000);
            }
            throw new Exception("Ended process still reported as a blocker.");
        }
        finally { if (!fixture.HasExited) fixture.Kill(); }
    }
    public static async Task RunAsync()
    {
        string name = "PersonalTools.DisplayPower." + Guid.NewGuid().ToString("N");
        using var parent = Process.GetCurrentProcess();
        using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(AppContext.BaseDirectory, "DisplayPowerProbe.ps1"), "-PipeName", name, "-ParentId", parent.Id.ToString(),
            "-ParentStartTicks", parent.StartTime.ToUniversalTime().Ticks.ToString() }) info.ArgumentList.Add(arg);
        using var helper = Process.Start(info)!;
        var stderr = helper.StandardError.ReadToEndAsync();
        var stdout = helper.StandardOutput.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            var result = await HdrWire.ReadAsync<JsonElement>(pipe, deadline.Token);
            int exit = result.GetProperty("ExitCode").GetInt32();
            string output = result.GetProperty("Output").GetString()!;
            if (exit == 0) DisplayPowerParser.Parse(output);
            else if (string.IsNullOrWhiteSpace(output) && result.GetProperty("Error").ValueKind == JsonValueKind.Null)
                throw new Exception("Failed power query omitted diagnostics.");
            pipe.Dispose();
            await helper.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stderr, stdout);
            Console.WriteLine($"PASS actual PowerShell helper framing, Windows query (exit {exit}), and exit after pipe disconnect");
        }
        finally { if (!helper.HasExited) helper.Kill(true); }
    }
}
