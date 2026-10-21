using System.Diagnostics;
using System.Runtime.InteropServices;
using PersonalTools;

internal static class ConsoleWindowChecks
{
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);

    public static async Task<bool> HandleAsync(string[] args)
    {
        if (args.Contains("--console-leaf"))
        {
            var window = GetConsoleWindow();
            Console.WriteLine($"{window}:{IsWindowVisible(window)}");
            return true;
        }
        if (args.Contains("--console-child"))
        {
            FreeConsole();
            AllocConsole();
            var window = GetConsoleWindow();
            Console.WriteLine($"{window}:{IsWindowVisible(window)}");
            // Model a CLI dependency that launches a console program with default flags.
            using var leaf = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--console-leaf")
                { UseShellExecute = false, RedirectStandardOutput = true })!;
            Console.Write(await leaf.StandardOutput.ReadToEndAsync());
            await leaf.WaitForExitAsync();
            return true;
        }
        if (!args.Contains("--console-host")) return false;
        // This host is launched without a console, as the installed WinUI app is.
        using var child = AntigravityCli.CreateProcess(new(Environment.ProcessPath!), ["--console-child"]);
        child.Start();
        child.StandardInput.Close();
        Console.Write(await child.StandardOutput.ReadToEndAsync());
        await child.WaitForExitAsync();
        return true;
    }

    public static async Task VerifyAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--console-host")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        string output = await host.StandardOutput.ReadToEndAsync();
        await host.WaitForExitAsync();
        string[] windows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (host.ExitCode != 0 || windows.Length != 2 || windows[0].StartsWith("0:")
            || windows[0] != windows[1] || !windows[0].EndsWith(":False"))
            throw new Exception("CLI and its child must share a hidden console: " + output);
        Console.WriteLine("PASS: CLI and descendant share a hidden console from a console-free host");
    }
}
