using System.Diagnostics;

namespace PersonalTools;

internal sealed record MicrophoneRecoveryNotice(string Title, string Text, bool Warning, string[] Relaunch);

// repair-windows-microphone.ps1 runs as SYSTEM and leaves one line in microphone-recovery-status.txt
// (Program Files, admin-only). The app turns each new line into one tray notification and, once the
// device was restarted, starts the vendor helpers the script had to close (MOTU's control panel) again
// in the user's session.
internal static class MicrophoneRecovery
{
    public static MicrophoneRecoveryNotice? Describe(string status)
    {
        status = status.Trim();
        // The script first reconnects the USB port (only the microphone drops) and restarts
        // Windows Audio only when that fails.
        if (status.StartsWith("Recovering microphone", StringComparison.Ordinal) && status.Contains("restarting Windows Audio", StringComparison.Ordinal))
            return new("마이크 복구 중", "USB 포트를 다시 연결하지 못해 Windows 오디오와 마이크 장치를 다시 시작합니다. 20초쯤 모든 소리가 끊깁니다.", false, []);
        if (status.StartsWith("Recovering microphone", StringComparison.Ordinal))
            return new("마이크 복구 중", "마이크 입력이 멈춰 마이크의 USB 연결을 다시 시작합니다. 10초쯤 마이크만 끊기고 다른 소리는 그대로 나옵니다.", false, []);
        if (status.StartsWith("Recovered microphone", StringComparison.Ordinal))
            return new("마이크 복구됨", "마이크 입력이 다시 들어옵니다. 통화 앱에서 마이크가 안 들리면 입력 장치를 다시 선택해 주세요.", false, Closed(status));
        if (status.StartsWith("Microphone recovery failed", StringComparison.Ordinal))
            return new("마이크 복구 실패", "USB 장치를 다시 시작하지 못했습니다. 마이크 USB 케이블을 다시 꽂아 주세요.", true, Closed(status));
        if (status.StartsWith("Microphone recovery paused", StringComparison.Ordinal))
            return new("마이크 자동 복구 멈춤", "1시간 안에 4번 다시 시작했는데도 또 멈췄습니다. 마이크 USB 케이블을 다시 꽂아 주세요.", true, []);
        if (status.StartsWith("Microphone recovery needs attention", StringComparison.Ordinal))
            return new("마이크 자동 복구 오류", status, true, []);
        return null;
    }
    // Only programs installed under Program Files; the status file is admin-only, this is a second fence.
    private static string[] Closed(string status)
    {
        int at = status.IndexOf("Closed: ", StringComparison.Ordinal);
        if (at < 0) return [];
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
        return status[(at + 8)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                roots.Any(root => root.Length > 0 && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static void Relaunch(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                // By name: MOTUMSeries.exe is 32-bit, whose image path this 64-bit app cannot always read.
                var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
                foreach (var process in running) process.Dispose();
                if (running.Length > 0) continue;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) })?.Dispose();
            }
            catch { /* A helper that does not start again only loses its control panel, not the audio. */ }
        }
    }
}
