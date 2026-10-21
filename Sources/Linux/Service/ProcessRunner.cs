using System.Diagnostics;
using System.Text;
namespace PersonalTools;
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output, string Error);
    public static async Task<Result> RunAsync(string executable, IEnumerable<string> args, CancellationToken token = default, int seconds = 30)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); process.StandardInput.Close();
        using var kill = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        async Task<string> Read(StreamReader reader)
        {
            var output = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer, timeout.Token)) > 0) { if (output.Length + count > 1024 * 1024) { process.Kill(true); throw new IOException("Process output exceeded 1 MiB."); } output.Append(buffer, 0, count); }
            return output.ToString();
        }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
        return new(process.ExitCode, stdout.Result, stderr.Result);
    }
}
