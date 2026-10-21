using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using PersonalTools;

internal static class LiveChecks
{
    private static async Task<HdrSnapshot> Request(HdrRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", HdrWire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000, timeout.Token);
        await HdrWire.WriteAsync(pipe, request, timeout.Token);
        return await HdrWire.ReadAsync<HdrSnapshot>(pipe, timeout.Token);
    }
    private static async Task<HdrSnapshot> Until(Func<HdrSnapshot, bool> condition, string description)
    {
        var clock = Stopwatch.StartNew();
        string? error = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                var state = await Request(new("read"));
                error = state.Error;
                if (condition(state)) return state;
            }
            catch (Exception e) { error = e.Message; }
            await Task.Delay(100);
        }
        throw new Exception(description + ": " + error);
    }
    private static void StopApp(string path)
    {
        foreach (var process in Process.GetProcessesByName("PersonalToolsWindows"))
        {
            using (process)
            {
                if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill(); process.WaitForExit(10000);
            }
        }
    }
    private static void StartApp(string path) => Process.Start(new ProcessStartInfo(path, "--startup") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    public static async Task RunAsync()
    {
        string app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PersonalTools", "ui", "PersonalToolsWindows.exe");
        var baseline = await Request(new("read"));
        if (baseline.Programs.Any(p => p.Running)) throw new Exception("Close registered games before hardware tests.");
        var primary = baseline.Monitors.First(m => m.Primary && m.Supported && m.Error is null);
        var secondary = baseline.Monitors.FirstOrDefault(m => !m.Primary && m.Supported && m.Error is null) ?? primary;
        var native = new HdrDisplay();
        string fixture = Path.Combine(AppContext.BaseDirectory, "HdrFixture.exe");
        string settings = Path.Combine(HdrFiles.DirectoryPath, "programs.json");
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        bool settingsChanged = false;
        Process? child = null;
        try
        {
            var switched = await Request(new("set", secondary.Id, !secondary.Enabled));
            if (switched.Error is not null || switched.Monitors.First(m => m.Id == secondary.Id).Enabled == secondary.Enabled)
                throw new Exception("Physical HDR switch failed: " + switched.Error);
            await Request(new("set", secondary.Id, secondary.Enabled));
            await Until(s => s.Monitors.First(m => m.Id == secondary.Id).Enabled == secondary.Enabled, "Manual restore");
            Console.WriteLine("PASS physical per-monitor HDR toggle and restore: " + secondary.Name);

            File.Copy(Path.Combine(AppContext.BaseDirectory, "HdrTests.exe"), fixture, true);
            var programs = originalSettings is null ? new List<HdrProgram>() : JsonSerializer.Deserialize<List<HdrProgram>>(originalSettings)!;
            var test = new HdrProgram(Guid.NewGuid(), "HDR live verification (temporary)", fixture);
            programs.Add(test);
            StopApp(app);
            settingsChanged = true;
            HdrFiles.Write("programs.json", programs);
            StartApp(app);
            await Until(s => s.Programs.Any(p => p.Program.Id == test.Id), "Load test profile");
            child = Process.Start(new ProcessStartInfo(fixture, "--fixture") { UseShellExecute = false, CreateNoWindow = true })!;
            await Until(s => s.Programs.Any(p => p.Program.Id == test.Id && p.Running) && s.Monitors.First(m => m.Id == primary.Id).Enabled, "Automatic HDR");
            await child.WaitForExitAsync(); child.Dispose(); child = null;
            await Until(s => s.Programs.All(p => p.Program.Id != test.Id || !p.Running) && s.Monitors.First(m => m.Id == primary.Id).Enabled == primary.Enabled, "Game exit restoration");
            Console.WriteLine("PASS actual process detection, primary HDR activation, exit restoration: " + primary.Name);

            child = Process.Start(new ProcessStartInfo(fixture, "--fixture") { UseShellExecute = false, CreateNoWindow = true })!;
            await Until(s => s.Programs.Any(p => p.Program.Id == test.Id && p.Running) && s.Monitors.First(m => m.Id == primary.Id).Enabled, "Second game");
            // Explicit 'on', even when already on, relinquishes automatic restoration ownership.
            var manual = await Request(new("set", primary.Id, true));
            if (manual.Error is not null) throw new Exception(manual.Error);
            await child.WaitForExitAsync(); child.Dispose(); child = null;
            await Until(s => s.Programs.All(p => p.Program.Id != test.Id || !p.Running), "Second game exit");
            if (!(await Request(new("read"))).Monitors.First(m => m.Id == primary.Id).Enabled) throw new Exception("Manual setting was overwritten after exit.");
            Console.WriteLine("PASS manual setting retained after game exit");
        }
        finally
        {
            if (child is not null) { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } child.Dispose(); }
            if (settingsChanged)
            {
                StopApp(app);
                if (originalSettings is null) File.Delete(settings); else File.WriteAllBytes(settings, originalSettings);
            }
            // Restore only the two monitors this test can change, even after an assertion failure.
            try
            {
                foreach (var monitor in new[] { primary, secondary }.DistinctBy(m => m.Id)) native.Set(monitor.Id, monitor.Enabled);
            }
            finally
            {
                if (settingsChanged) StartApp(app);
                if (File.Exists(fixture)) File.Delete(fixture);
            }
        }
        var final = await Until(s => !s.Programs.Any(p => p.Program.Path == fixture), "Cleanup test profile");
        foreach (var monitor in baseline.Monitors)
            if (final.Monitors.FirstOrDefault(m => m.Id == monitor.Id)?.Enabled != monitor.Enabled) throw new Exception("Display state changed: " + monitor.Name);
        Console.WriteLine("PASS original monitor states restored, temporary profile removed, installed app running");
    }
}
