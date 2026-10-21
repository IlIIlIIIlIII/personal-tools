using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Win32;
using PersonalTools;

namespace PersonalTools.HdrWidget;

internal static class WidgetClient
{
    private static DateTime lastStart;
    public static async Task<HdrSnapshot> SendAsync(HdrRequest request)
        => await SendAsync<HdrRequest, HdrSnapshot>(HdrWire.PipeName, request);
    public static async Task<VllmSnapshot> SendVllmAsync(string command)
        => await SendAsync<string, VllmSnapshot>(VllmProtocol.PipeName, command);
    private static async Task<TResponse> SendAsync<TRequest, TResponse>(string pipeName, TRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(250, timeout.Token); }
        catch (TimeoutException)
        {
            if (DateTime.UtcNow - lastStart > TimeSpan.FromSeconds(15))
            {
                using var key = Registry.CurrentUser.OpenSubKey(HdrWire.RegistryPath);
                var path = key?.GetValue("AppPath") as string;
                if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path))
                    throw new IOException("Personal Tools 앱을 먼저 한 번 실행한 뒤 다시 시도하세요.");
                Process.Start(new ProcessStartInfo(path, "--startup") { UseShellExecute = false, CreateNoWindow = true });
                lastStart = DateTime.UtcNow;
            }
            await pipe.ConnectAsync(8000, timeout.Token);
        }
        await HdrWire.WriteAsync(pipe, request, timeout.Token);
        return await HdrWire.ReadAsync<TResponse>(pipe, timeout.Token);
    }
}
